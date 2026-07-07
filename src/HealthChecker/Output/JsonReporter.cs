using System.Text.Json;
using System.Text.Json.Serialization;
using HealthChecker.Models;

namespace HealthChecker.Output;

/// <summary>
/// Writes the health check report as indented JSON to stdout.
/// Useful for CI pipelines and machine consumption (--json flag).
/// </summary>
class JsonReporter(TextWriter? output = null) : IReporter
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Report(HealthCheckReport report)
    {
        var writer  = output ?? Console.Out;
        var payload = new
        {
            timestamp      = DateTime.UtcNow.ToString("o"),
            config         = report.ConfigPath,
            filter_region  = report.FilterRegion,
            filter_service = report.FilterService,
            exit_code      = report.ExitCode,
            summary = new
            {
                total   = report.TotalInstances,
                healthy = report.HealthyInstances,
                failed  = report.TotalInstances - report.HealthyInstances,
            },
            catalog_events = report.CatalogEvents.Select(e => new
            {
                service = e.SvcName,
                region  = e.Region,
                success = e.Success,
                summary = e.Summary,
                error   = e.Error,
            }),
            http_results = report.HttpResults
                .GroupBy(e => new { e.Region, e.Svc.CatalogName })
                .OrderBy(g => g.Key.Region).ThenBy(g => g.Key.CatalogName)
                .Select(g => new
                {
                    region  = g.Key.Region,
                    service = g.Key.CatalogName,
                    checks  = g.OrderBy(e => e.Result.Url).Select(e => new
                    {
                        url        = e.Result.Url,
                        healthy    = e.Result.Healthy,
                        elapsed_ms = e.Result.ElapsedMs,
                        error      = e.Result.Error,
                    }),
                }),
            argocd_results = report.ArgocdResults.Select(e => new
            {
                region             = e.Region,
                service            = e.SvcName,
                app_name           = e.AppName,
                status             = e.Status,
                pod_count          = e.PodCount,
                degraded_resources = e.DegradedResources.Length > 0 ? e.DegradedResources : null,
            }),
            token_warning    = report.TokenWarning,
            no_token_message = report.NoTokenMessage,
            watch_run        = report.WatchRun,
            // null  → run 1 (no prior run to compare)
            // []    → subsequent run, no status changes
            // [...] → subsequent run, list of flipped URLs
            watch_changes = report.WatchChanges?.Select(c => new
            {
                service = c.Service,
                region  = c.Region,
                url     = c.Url,
                healthy = c.Healthy, // true = recovered, false = newly failed
            }),
        };

        writer.WriteLine(JsonSerializer.Serialize(payload, Opts));
    }
}
