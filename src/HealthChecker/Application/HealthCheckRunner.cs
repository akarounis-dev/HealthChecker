using System.Collections.Concurrent;
using System.Net.Http.Headers;
using HealthChecker.Checks;
using HealthChecker.Clients;
using HealthChecker.Models;

namespace HealthChecker.Application;

/// <summary>
/// Orchestrates the full health-check pipeline and returns a <see cref="HealthCheckReport"/>.
/// This class has zero console output - all presentation is the caller's responsibility.
/// </summary>
class HealthCheckRunner(Config config, RunOptions options)
{
    readonly HashSet<string> _mockFail     = options.MockFailRegions     ?? [];
    readonly HashSet<string> _mockArgoFail = options.MockArgoFailRegions ?? [];

    bool MockFails    (string region) => _mockFail    .Contains("*") || _mockFail    .Contains(region);
    bool MockArgoFails(string region) => _mockArgoFail.Contains("*") || _mockArgoFail.Contains(region);

    public async Task<HealthCheckReport> RunAsync()
    {
        // ── 0. Service filter ─────────────────────────────────────────────────

        var activeServices = options.FilterService is null
            ? config.Services
            : config.Services
                .Where(s => s.ServiceAlias.Equals(options.FilterService, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        // ── 1. ArgoCD token resolution ────────────────────────────────────────

        string? argoToken      = null;
        string? noTokenMessage = null;
        string? tokenWarning   = null;

        if (!options.SkipArgocd && config.Catalog?.ArgocdServer is not null)
        {
            (argoToken, noTokenMessage) = await ArgocdClient.ResolveTokenAsync(
                new Uri(config.Catalog.ArgocdServer).Host, config.Catalog.ArgocdToken);

            if (argoToken is not null)
            {
                var timeLeft = ArgocdClient.GetTokenTimeLeft(argoToken);
                if (timeLeft is null)
                {
                    // Not a valid JWT - bail out before making any ArgoCD calls.
                    tokenWarning = "ArgoCD token is not a valid JWT - check argocd_token in your config.";
                    argoToken    = null;
                }
                else if (timeLeft <= TimeSpan.Zero)
                {
                    tokenWarning = "ArgoCD token has expired - update argocd_token in your config.";
                    argoToken    = null;
                }
                else if (timeLeft <= TimeSpan.FromHours(24))
                {
                    tokenWarning = $"ArgoCD token expires in {timeLeft.Value.TotalHours:F0}h - refresh it soon.";
                }
            }
        }

        // ── 2. Catalog resolution (parallel) ─────────────────────────────────

        var catalogEvents = new List<CatalogEvent>();

        // Pairs that failed catalog resolution - their HTTP checks are skipped
        // rather than aborting the entire run.
        var failedCatalogPairs = new HashSet<(string, string)>();

        if (config.Catalog is not null)
        {
            var lookups = activeServices
                .SelectMany(svc => svc.Regions
                    .Where(kvp =>
                        kvp.Value.CatalogPlatform is not null &&
                        kvp.Value.CatalogRegion   is not null &&
                        kvp.Value.KubernetesUrl   is null     &&
                        kvp.Value.VmTargets       is null     &&
                        (options.FilterRegion is null ||
                         kvp.Key.Equals(options.FilterRegion, StringComparison.OrdinalIgnoreCase)))
                    .Select(kvp => (Svc: svc, RegionName: kvp.Key, Rc: kvp.Value)))
                .ToList();

            using var catalogHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var argoHttp    = argoToken is not null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : null;
            if (argoHttp is not null)
                argoHttp.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", argoToken!);

            // Fan out all catalog lookups; Task.WhenAll preserves order.
            var resolved = await Task.WhenAll(
                lookups.Select(l => ResolveCatalogAsync(catalogHttp, argoHttp, l.Svc, l.RegionName, l.Rc, argoToken)));

            // Apply results sequentially - no concurrent dictionary mutation.
            foreach (var (svc, regionName, updatedRc, evt) in resolved)
            {
                catalogEvents.Add(evt);
                if (updatedRc is not null)
                    svc.Regions[regionName] = updatedRc;
                else
                    failedCatalogPairs.Add((svc.CatalogName, regionName));
            }
        }

        // ── 3. Build flat check list ──────────────────────────────────────────

        var defaults = config.Defaults ?? new Defaults();

        var checks = new List<(ServiceConfig Svc, string Region, string Url, int Retries, int TimeoutSec)>();

        foreach (var svc in activeServices)
        {
            foreach (var (regionName, rc) in svc.Regions
                .Where(kvp =>
                    !failedCatalogPairs.Contains((svc.CatalogName, kvp.Key)) &&
                    (options.FilterRegion is null ||
                     kvp.Key.Equals(options.FilterRegion, StringComparison.OrdinalIgnoreCase))))
            {
                var retries    = (rc.RetryAttempts  ?? svc.RetryAttempts)  ?? defaults.RetryAttempts;
                var timeoutSec = (rc.TimeoutSeconds ?? svc.TimeoutSeconds) ?? defaults.TimeoutSeconds;

                // Wrap ExpandTargets exceptions as catalog failures rather than letting them
                // propagate and abort the entire run.
                try
                {
                    checks.AddRange(TargetUrlBuilder.ExpandTargets(
                        svc, regionName, rc, retries, timeoutSec,
                        config.Catalog?.Environment, config.Catalog?.KubernetesDomain));
                }
                catch (Exception ex)
                {
                    catalogEvents.Add(new CatalogEvent(svc.CatalogName, regionName, false, null,
                        $"Target expansion failed: {ex.Message}"));
                }
            }
        }

        // ── 4. HTTP health checks (parallel) ─────────────────────────────────

        var bag = new ConcurrentBag<CheckEntry>();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HealthChecker/1.0");

        // Cap concurrent HTTP checks to avoid overwhelming the network on large fleets.
        using var semaphore = new SemaphoreSlim(50);

        await Task.WhenAll(checks.Select(async c =>
        {
            await semaphore.WaitAsync();
            try
            {
                var r = await Checker.CheckUrl(http, c.Url, c.Svc.HealthcheckContains,
                                               c.Retries, c.TimeoutSec, defaults.RetryDelayMs);
                bag.Add(new CheckEntry(c.Svc, c.Region, r));
            }
            finally
            {
                semaphore.Release();
            }
        }));

        var httpResults = bag.ToList();

        // Apply mock HTTP failures
        if (_mockFail.Count > 0)
            httpResults = httpResults.Select(e => MockFails(e.Region)
                ? new CheckEntry(e.Svc, e.Region,
                    new CheckResult(e.Result.Url, false, 0, "[mock] --mock-fail applied"))
                : e).ToList();

        // ── 5. ArgoCD pod health (parallel) ──────────────────────────────────

        var argoResults = new List<ArgocdEntry>();

        if (!options.SkipArgocd && config.Catalog?.ArgocdServer is string argoServer)
        {
            var tokenUsable = argoToken is not null || _mockArgoFail.Count > 0;

            if (tokenUsable)
            {
                var k8sRegions = activeServices
                    .SelectMany(svc => svc.Regions
                        .Where(kvp =>
                            (options.FilterRegion is null ||
                             kvp.Key.Equals(options.FilterRegion, StringComparison.OrdinalIgnoreCase)) &&
                            (kvp.Value.KubernetesCluster is not null ||
                             kvp.Value.KubernetesUrl      is not null) &&
                            kvp.Value.CatalogPlatform is not null &&
                            kvp.Value.CatalogRegion   is not null)
                        .Select(kvp => (Svc: svc, Region: kvp.Key)))
                    .ToList();

                using var argoHealthHttp = argoToken is not null
                    ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : null;
                if (argoHealthHttp is not null)
                    argoHealthHttp.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", argoToken!);

                argoResults = [.. await Task.WhenAll(
                    k8sRegions
                        .Where(p => MockArgoFails(p.Region) || argoToken is not null)
                        .Select(async p =>
                        {
                            var (svc, regionName) = p;
                            var rc      = svc.Regions[regionName];
                            var appName = $"{svc.CatalogName}-{rc.CatalogPlatform}-{rc.CatalogRegion}-{config.Catalog.Environment}";
                            var argoUrl = $"{argoServer.TrimEnd('/')}/applications/{appName}";

                            string   status;
                            string[] degraded;
                            int      podCount;

                            if (MockArgoFails(regionName))
                            {
                                status   = "Degraded";
                                degraded = [$"Deployment/{appName} [Degraded]", $"ReplicaSet/{appName}-mock [Progressing]"];
                                podCount = 3;
                            }
                            else
                            {
                                (status, degraded, podCount) = await ArgocdClient.GetAppHealth(
                                    argoServer, argoToken!, appName, http: argoHealthHttp);
                            }

                            return new ArgocdEntry(regionName, svc.CatalogName, appName, argoUrl, status, degraded, podCount);
                        }))];
            }
        }

        return BuildReport(catalogEvents, httpResults, argoResults, tokenWarning, noTokenMessage);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches catalog data for one (svc, region) pair and optionally resolves the k8s
    /// ingress URL from ArgoCD. Pure: no side effects on shared state; caller applies result.
    /// </summary>
    async Task<(ServiceConfig Svc, string RegionName, RegionConfig? UpdatedRc, CatalogEvent Event)>
        ResolveCatalogAsync(HttpClient http, HttpClient? argoHttp, ServiceConfig svc, string regionName, RegionConfig rc, string? argoToken)
    {
        try
        {
            var result = await CatalogClient.FetchInstances(
                http,
                config.Catalog!.BaseUrl,
                svc.CatalogName,
                options.CatalogVersion,
                config.Catalog.Environment,
                rc.CatalogPlatform!,
                rc.CatalogRegion!);

            var updatedRc = rc;
            var parts     = new List<string> { $"[{result.ResolvedVersion}]" };

            if (result.VmTargets.Length > 0)
            {
                updatedRc = updatedRc with { VmTargets = result.VmTargets };
                parts.Add($"{result.VmTargets.Length} VM(s)");
            }

            if (result.KubernetesCluster is string cluster)
            {
                updatedRc = updatedRc with { KubernetesCluster = cluster };

                string? k8sUrl    = null;
                string  k8sSource = "constructed";

                if (argoToken is not null && argoHttp is not null)
                {
                    var appName = $"{svc.CatalogName}-{rc.CatalogPlatform}-{rc.CatalogRegion}-{config.Catalog.Environment}";
                    var (ingressUrl, ingressReason) = await ArgocdClient.GetAppIngress(
                        config.Catalog.ArgocdServer!, argoToken, appName, http: argoHttp);

                    if (ingressUrl is not null)
                    {
                        k8sUrl    = ingressUrl;
                        k8sSource = "argo";
                    }
                    else
                    {
                        parts.Add($"ArgoCD ingress: {ingressReason}");
                    }
                }

                k8sUrl ??= $"http://{svc.CatalogName}.{cluster}.{config.Catalog.Environment}-{config.Catalog.KubernetesDomain}/";
                updatedRc = updatedRc with { KubernetesUrl = k8sUrl };
                parts.Add($"k8s [{k8sSource}] -> {k8sUrl}");
            }

            return (svc, regionName, updatedRc,
                new CatalogEvent(svc.CatalogName, regionName, true, string.Join("  ", parts), null));
        }
        catch (Exception ex)
        {
            return (svc, regionName, null,
                new CatalogEvent(svc.CatalogName, regionName, false, null, ex.Message));
        }
    }

    HealthCheckReport BuildReport(
        List<CatalogEvent> catalogEvents,
        List<CheckEntry>   httpResults,
        List<ArgocdEntry>  argoResults,
        string?            tokenWarning,
        string?            noTokenMessage)
        => new(
            options.ConfigPath,
            options.FilterRegion,
            catalogEvents,
            httpResults,
            argoResults,
            tokenWarning,
            noTokenMessage,
            config.Kibana,
            options.FilterService,
            config.Defaults?.ResponseTimeWarnMs);

}
