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

        // Find all versions that have a deployment for (env, platform, region), sorted newest first.
        var candidatesWithTime = allVersions
            .Select(sv => new { Node = sv, Latest = GetLatestEventTime(sv, catalogEnv, catalogPlatform, catalogRegion) })
            .Where(x => x.Latest.HasValue)
            .OrderByDescending(x => x.Latest)
            .ToList();

        if (candidatesWithTime.Count == 0)
            throw new InvalidOperationException(
                $"No deployment found for {catalogEnv}/{catalogPlatform}/{catalogRegion} in catalog [{serviceName}].");

        // The most-recent version is the canonical release (e.g. "1.70.2.37").
        // Hybrid setups deploy the same release to multiple hosting types under version
        // strings like "1.70.2.37-{commitSHA}"
        var primaryVersion = candidatesWithTime[0].Node?["version"]?.GetValue<string>() ?? "";
        var baseVersion    = StripCommitSuffix(primaryVersion);

        var matchingVersions = candidatesWithTime
            .Where(x =>
            {
                var v = x.Node?["version"]?.GetValue<string>() ?? "";
                return v == baseVersion || v.StartsWith(baseVersion + "-");
            })
            .Select(x => x.Node)
            .ToList();

        // Version label: collapse to a single display string.
        var resolvedVersion = string.Join(", ", matchingVersions
            .Select(sv => sv?["version"]?.GetValue<string>() ?? "(unknown)")
            .Distinct());

        // Union all targets from the matching versions' events for this region.
        var allTargets = matchingVersions
            .SelectMany(sv => sv?["deployments"]?["environments"]?.AsArray()
                .Where(e =>
                    e?["name"]?.GetValue<string>()     == catalogEnv      &&
                    e?["platform"]?.GetValue<string>() == catalogPlatform &&
                    e?["region"]?.GetValue<string>()   == catalogRegion)
                .SelectMany(e => e?["events"]?.AsArray() ?? [])
                .SelectMany(evt => evt?["targets"]?.AsArray() ?? [])
                .Select(t => t?.GetValue<string>() ?? "") ?? [])
            .Where(t => t.Length > 0)
            .Distinct()
            .ToArray();

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

    /// <summary>
    /// Strips a trailing commit-SHA suffix from a version string.
    /// "1.70.2.37-4f694a1" → "1.70.2.37"; "1.70.2.37" → "1.70.2.37".
    /// A suffix is recognised as a commit SHA when it consists entirely of hex
    /// characters and is between 7 and 40 characters long.
    /// </summary>
    internal static string StripCommitSuffix(string version)
    {
        var lastHyphen = version.LastIndexOf('-');
        if (lastHyphen < 0) return version;

        var suffix = version[(lastHyphen + 1)..];
        var isCommitSha = suffix.Length is >= 7 and <= 40
            && suffix.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

        return isCommitSha ? version[..lastHyphen] : version;
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
