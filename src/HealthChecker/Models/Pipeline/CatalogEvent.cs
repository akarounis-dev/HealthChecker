namespace HealthChecker.Models;

/// <summary>Outcome of resolving one service/region from the catalog.</summary>
record CatalogEvent(
    string  SvcName,
    string  Region,
    bool    Success,
    string? Summary, // e.g. "[1.2.3]  2 VM(s)  k8s [argo] -> http://..."
    string? Error    // null on success
);
