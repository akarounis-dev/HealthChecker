namespace HealthChecker.Tests.Helpers;

/// <summary>
/// Loads ServiceKatalog API response fixtures from Fixtures/catalog/.
/// Constants expose the values embedded in each file so tests can assert against them
/// without re-hardcoding the same strings.
/// </summary>
static class CatalogJson
{
    // ── Values present in the fixture files ───────────────────────────────────

    public static readonly string[] VmTargets      = ["prd-spt09-01", "prd-spt09-02", "prd-spt09-03"];
    public const string             VmVersion       = "1.0.0";
    public const string             K8sCluster      = "eur-euw-prd-aks02";
    public const string             K8sVersion      = "2.0.0";
    public const string             HybridVersion   = "3.0.0";
    public const string             LatestVersion        = "2.0.0";          // two-versions fixture: only version matched (base-version filter excludes 1.0.0)
    public const string             OlderVersion         = "1.0.0";          // two-versions fixture: excluded by base-version filter
    public const string             CrossVersionDocker   = "1.70.2.37";      // cross-version hybrid: Docker version
    public const string             CrossVersionAks      = "1.70.2.37-4f694a1"; // cross-version hybrid: AKS version
    public static readonly string[] CrossVersionVmTargets =
        ["prd-spt14-01", "prd-spt14-02", "prd-spt14-03", "prd-spt14-04", "prd-spt14-05", "prd-spt14-06"];

    // ── Fixture loaders ───────────────────────────────────────────────────────

    /// <summary>Single version with three VM targets (numeric suffix).</summary>
    public static string VmTargetsResponse()       => Load("catalog/response_vm_targets.json");

    /// <summary>Single version with one k8s cluster target (non-numeric suffix).</summary>
    public static string K8sClusterResponse()      => Load("catalog/response_k8s_cluster.json");

    /// <summary>Single version with both VM targets and a k8s cluster target.</summary>
    public static string HybridResponse()          => Load("catalog/response_hybrid.json");

    /// <summary>Two versions with different event times; both are collected, newer appears first in label.</summary>
    public static string TwoVersionsResponse()          => Load("catalog/response_two_versions.json");

    /// <summary>Two versions for the same region: one with Docker VMs, one with a k8s cluster (real-world hybrid).</summary>
    public static string CrossVersionHybridResponse()   => Load("catalog/response_cross_version_hybrid.json");

    /// <summary>Response with an empty service_version array.</summary>
    public static string EmptyResponse()           => Load("catalog/response_empty.json");

    /// <summary>Response whose only environment entry does not match prd/eur/euw.</summary>
    public static string NoMatchingEnvResponse()   => Load("catalog/response_no_matching_env.json");

    /// <summary>VM targets returned in non-sorted order - used to verify sort behavior.</summary>
    public static string UnsortedTargetsResponse() => Load("catalog/response_unsorted_targets.json");

    // ── Loader ────────────────────────────────────────────────────────────────

    static string Load(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));
}
