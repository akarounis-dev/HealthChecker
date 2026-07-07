namespace HealthChecker.Models;

/// <summary>CLI / invocation options parsed from args before the runner executes.</summary>
record RunOptions(
    string            ConfigPath,
    string?           FilterRegion        = null,
    string?           FilterService       = null,
    string?           CatalogVersion      = null,
    bool              SkipArgocd          = false,
    HashSet<string>?  MockFailRegions     = null,
    HashSet<string>?  MockArgoFailRegions = null
);
