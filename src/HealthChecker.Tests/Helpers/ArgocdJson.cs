namespace HealthChecker.Tests.Helpers;

/// <summary>
/// Loads ArgoCD API response fixtures from Fixtures/argocd/.
/// Constants expose the values embedded in each file so tests can assert against them
/// without re-hardcoding the same strings.
/// </summary>
static class ArgocdJson
{
    // ── Values present in the fixture files ───────────────────────────────────
    // Reference these in test assertions instead of duplicating the raw strings.

    public const string DegradedDeployment  = "web-sportsbookfeed";
    public const string DegradedReplicaSet  = "web-sportsbookfeed-7d9f8b";
    public const int    ResourceTreePods3   = 3;
    public const int    ResourceTreePods5   = 5;
    public const string IngressHost         = "web-sportsbookfeed.eur-euw-prd.novibet.systems";
    public const string IngressRouteHost    = "web-sportsbookfeed.k8s.eur-euw-prd.novibet.systems";
    public const string UnauthorizedMessage = "permission denied";

    // ── Fixture loaders ───────────────────────────────────────────────────────

    public static string AppHealthy()         => Load("argocd/app_healthy.json");
    public static string AppDegraded()        => Load("argocd/app_degraded.json");

    /// <summary>
    /// Degraded app where one resource is Healthy and one is Missing -
    /// used specifically to verify the filter that excludes those two statuses.
    /// </summary>
    public static string AppHealthFilterTest() => Load("argocd/app_health_filter_test.json");

    public static string ResourceTree() => Load("argocd/resource_tree_empty.json");

    public static string ResourceTree(int podCount) => podCount switch
    {
        3 => Load("argocd/resource_tree_3pods.json"),
        5 => Load("argocd/resource_tree_5pods.json"),
        _ => throw new ArgumentException(
                 $"No fixture for podCount={podCount}. Add Fixtures/argocd/resource_tree_{podCount}pods.json.")
    };

    /// <summary>Resource tree containing Deployment, Service, ConfigMap - no ingress.</summary>
    public static string ResourceTreeKinds()         => Load("argocd/resource_tree_kinds.json");
    public static string ResourceTreeWithIngress()   => Load("argocd/resource_tree_ingress.json");
    public static string ResourceTreeWithIngressRoute() => Load("argocd/resource_tree_ingress_route.json");
    public static string IngressManifest()           => Load("argocd/ingress_manifest.json");
    public static string IngressRouteManifest()      => Load("argocd/ingress_route_manifest.json");
    public static string ErrorUnauthorized()         => Load("argocd/error_unauthorized.json");

    // ── Loader ────────────────────────────────────────────────────────────────

    static string Load(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));
}
