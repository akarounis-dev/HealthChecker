namespace HealthChecker.Models;

/// <summary>Result of a single ArgoCD application health query.</summary>
record ArgocdEntry(
    string   Region,
    string   SvcName,
    string   AppName,
    string   ArgoUrl,
    string   Status,
    string[] DegradedResources,
    int      PodCount
);
