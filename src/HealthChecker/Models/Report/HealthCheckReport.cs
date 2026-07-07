namespace HealthChecker.Models;

/// <summary>
/// A single URL whose health status flipped since the previous watch-mode run.
/// Populated on run 2+ in watch mode; null on run 1 or in single-run mode.
/// </summary>
record WatchStatusChange(
    string Service,
    string Region,
    string Url,
    bool   Healthy   // true = recovered, false = newly failed
);

/// <summary>
/// Everything a reporter needs to render output - produced by HealthCheckRunner,
/// consumed by IReporter implementations.
/// </summary>
record HealthCheckReport(
    string                      ConfigPath,
    string?                     FilterRegion,
    IReadOnlyList<CatalogEvent> CatalogEvents,
    IReadOnlyList<CheckEntry>   HttpResults,
    IReadOnlyList<ArgocdEntry>  ArgocdResults,
    string?                     TokenWarning,   // non-null → expiry warning
    string?                     NoTokenMessage, // non-null → "no token found" instructions
    KibanaConfig?               Kibana,
    string?                     FilterService       = null, // non-null → single-service run
    int?                        ResponseTimeWarnMs  = null, // from config.Defaults; null = no threshold
    int?                        WatchRun            = null, // run index in --watch mode; null = single run
    IReadOnlyList<WatchStatusChange>? WatchChanges  = null  // null = run 1 (no prior), empty = no changes
)
{
    public bool CatalogFailed    => CatalogEvents.Any(e => !e.Success);
    public int  TotalInstances   => HttpResults.Count;
    public int  HealthyInstances => HttpResults.Count(x => x.Result.Healthy);
    public bool ArgocdFailed     => ArgocdResults.Any(x => x.Status != "Healthy" && x.Status != "Unknown");

    /// <summary>
    /// Number of healthy checks whose elapsed time exceeds the effective response-time threshold.
    /// Uses per-service override if set; falls back to the global <see cref="ResponseTimeWarnMs"/>.
    /// Zero when no threshold is configured anywhere.
    /// </summary>
    public int SlowInstances => HttpResults.Count(e =>
    {
        var threshold = e.Svc.ResponseTimeWarnMs ?? ResponseTimeWarnMs;
        return threshold.HasValue && e.Result.Healthy && e.Result.ElapsedMs > threshold.Value;
    });

    /// <summary>
    /// Bit-flag exit code for precise CI pipeline routing:
    ///   0 = all healthy
    ///   1 = one or more HTTP checks failed          (bit 0)
    ///   2 = one or more ArgoCD apps not Healthy     (bit 1)
    ///   4 = one or more catalog lookups failed      (bit 2)
    /// Bits combine freely - exit code 7 means all three conditions are true.
    /// Process errors (bad config path, parse failure) exit with 1 from Program.cs.
    /// </summary>
    public int ExitCode
    {
        get
        {
            int code = 0;
            if (CatalogFailed)                         code |= 4;
            if (TotalInstances - HealthyInstances > 0) code |= 1;
            if (ArgocdFailed)                          code |= 2;
            return code;
        }
    }
}
