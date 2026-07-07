using System.Text.Json.Nodes;
using HealthChecker.Models;

namespace HealthChecker.Clients;

static class CatalogClient
{
    public static async Task<CatalogResult> FetchInstances(
        HttpClient http,
        string baseUrl,
        string serviceName,
        string? version,
        string catalogEnv,
        string catalogPlatform,
        string catalogRegion)
    {
        var url = version is not null
            ? $"{baseUrl.TrimEnd('/')}/{serviceName}/{version}"
            : $"{baseUrl.TrimEnd('/')}/{serviceName}";

        JsonNode? doc;
        try
        {
            var json = await http.GetStringAsync(url);
            doc = JsonNode.Parse(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Catalog request failed [{serviceName}]: {ex.Message}", ex);
        }

        var allVersions = doc?["service_version"]?.AsArray()
            ?? throw new InvalidOperationException($"No service_version in catalog response for {serviceName}.");

        var matchingEntry = allVersions
            .Select(sv => new
            {
                Node        = sv,
                LatestEvent = GetLatestEventTime(sv, catalogEnv, catalogPlatform, catalogRegion)
            })
            .Where(x => x.LatestEvent.HasValue)
            .OrderByDescending(x => x.LatestEvent)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No deployment found for {catalogEnv}/{catalogPlatform}/{catalogRegion} in catalog [{serviceName}].");

        var resolvedVersion = matchingEntry.Node?["version"]?.GetValue<string>() ?? "(unknown)";

        var matchedEnv = matchingEntry.Node?["deployments"]?["environments"]?.AsArray()
            .FirstOrDefault(e =>
                e?["name"]?.GetValue<string>()     == catalogEnv      &&
                e?["platform"]?.GetValue<string>() == catalogPlatform &&
                e?["region"]?.GetValue<string>()   == catalogRegion);

        var allTargets = matchedEnv?["events"]?.AsArray()
            .SelectMany(evt => evt?["targets"]?.AsArray() ?? [])
            .Select(t => t?.GetValue<string>() ?? "")
            .Where(t => t.Length > 0)
            .Distinct()
            .ToArray() ?? [];

        // Split by last-segment type: numeric = VM instance, non-numeric = k8s cluster
        var vmTargets = allTargets
            .Where(t => int.TryParse(t.Split('-').Last(), out _))
            .OrderBy(t => t)
            .ToArray();

        var k8sCluster = allTargets
            .FirstOrDefault(t => !int.TryParse(t.Split('-').Last(), out _));

        if (vmTargets.Length == 0 && k8sCluster is null)
            throw new InvalidOperationException(
                $"No usable targets found for {catalogEnv}/{catalogPlatform}/{catalogRegion} in catalog [{serviceName}].");

        return new CatalogResult(resolvedVersion, vmTargets, k8sCluster);
    }

    private static DateTimeOffset? GetLatestEventTime(JsonNode? sv, string env, string platform, string region)
    {
        var times = sv?["deployments"]?["environments"]?.AsArray()
            .Where(e =>
                e?["name"]?.GetValue<string>()     == env      &&
                e?["platform"]?.GetValue<string>() == platform &&
                e?["region"]?.GetValue<string>()   == region)
            .SelectMany(e => e?["events"]?.AsArray() ?? [])
            .Select(evt =>
            {
                var s = evt?["event_time"]?.GetValue<string>();
                return s is not null && DateTimeOffset.TryParse(s, out var dt) ? dt : (DateTimeOffset?)null;
            })
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .ToList();

        return times is { Count: > 0 } ? times.Max() : null;
    }
}
