using HealthChecker.Models;
using Spectre.Console;

namespace HealthChecker.Output;

/// <summary>
/// Renders a <see cref="HealthCheckReport"/> to the terminal using Spectre.Console.
/// This is the only class in the codebase that is allowed to write to the console
/// (Spinner.cs, --list output in Program.cs, and watch-mode chrome are intentional exceptions).
/// </summary>
class ConsoleReporter : IReporter
{
    readonly IAnsiConsole _ansi;
    readonly TextWriter   _rawOut;  // for writing URLs that would crash Spectre's markup parser

    bool NoColor => _ansi.Profile.Capabilities.ColorSystem == ColorSystem.NoColors;

    /// <summary>Production constructor. Pass noColor: true to honour --no-color / NO_COLOR.</summary>
    public ConsoleReporter(bool noColor = false)
        : this(MakeAnsi(noColor), Console.Out) { }

    /// <summary>
    /// Constructor used by tests to inject a StringWriter-backed IAnsiConsole.
    /// Pass the same writer that backs the IAnsiConsole so Console.Out bypasses are also captured.
    /// </summary>
    public ConsoleReporter(IAnsiConsole ansi, TextWriter rawOut)
    {
        _ansi   = ansi;
        _rawOut = rawOut;
    }

    internal static IAnsiConsole MakeAnsi(bool noColor) => noColor
        ? AnsiConsole.Create(new AnsiConsoleSettings
          { ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(Console.Out) })
        : AnsiConsole.Console;

    /// <summary>
    /// Returns a Spectre markup string that renders as a clickable OSC-8 hyperlink in
    /// supporting terminals. Spectre measures only the visible text length, so word-wrap
    /// calculations stay correct regardless of terminal width.
    /// In no-color mode plain escaped text is returned — Spectre strips SGR codes but
    /// still emits OSC-8 link sequences even in NoColors mode, so we guard explicitly.
    /// </summary>
    string Link(string url)
    {
        if (NoColor) return Markup.Escape(url);
        // Percent-encode [ and ] so they can't confuse Spectre's markup tag parser when
        // embedded in the [link=url] attribute. The visible text still uses Markup.Escape
        // (which doubles them) so it renders correctly as display characters.
        var attrUrl = url.Replace("[", "%5B").Replace("]", "%5D");
        return $"[link={attrUrl}]{Markup.Escape(url)}[/]";
    }

    /// <summary>
    /// Returns an OSC-8 hyperlink string for use with Console.Out (bypassing Spectre),
    /// or the plain URL when no-color mode is active.
    /// Used for Kibana URLs whose RISON content crashes Spectre's markup parser.
    /// </summary>
    string RawLink(string url) => NoColor
        ? url
        : $"\x1b]8;;{url}\x1b\\{url}\x1b]8;;\x1b\\";

    public void Report(HealthCheckReport report)
    {
        PrintCatalogEvents(report.CatalogEvents);

        if (report.TotalInstances == 0)
        {
            _ansi.WriteLine("No instances match the given filters.");
            PrintTokenMessages(report.TokenWarning, report.NoTokenMessage);
            if (report.ArgocdResults.Count > 0)
                PrintArgocdResults(report.ArgocdResults);
            return;
        }

        PrintHeader(report.ConfigPath, report.TotalInstances, report.FilterRegion, report.FilterService);
        // Token is effectively unavailable when it expired, was invalid, or was never found.
        var argoTokenMissing = report.NoTokenMessage is not null
            || (report.TokenWarning is not null &&
                (report.TokenWarning.Contains("expired") || report.TokenWarning.Contains("valid JWT")));

        PrintHttpResults(report.HttpResults, report.Kibana, report.ResponseTimeWarnMs, argoTokenMissing);
        PrintTokenMessages(report.TokenWarning, report.NoTokenMessage);

        if (report.ArgocdResults.Count > 0)
            PrintArgocdResults(report.ArgocdResults);

        PrintSummary(report.HealthyInstances, report.TotalInstances, report.SlowInstances, report.ResponseTimeWarnMs);
    }

    // ── Catalog resolution ────────────────────────────────────────────────────

    void PrintCatalogEvents(IReadOnlyList<CatalogEvent> events)
    {
        if (events.Count == 0) return;

        foreach (var e in events)
        {
            _ansi.Markup($"  Fetching {Markup.Escape(e.SvcName)} [[{Markup.Escape(e.Region)}]] ... ");
            if (e.Success)
                _ansi.MarkupLine($"[green]{Markup.Escape(e.Summary ?? "")}[/]");
            else
            {
                _ansi.MarkupLine("[red]FAILED[/]");
                _ansi.MarkupLine($"[maroon]    {Markup.Escape(e.Error ?? "")}[/]");
            }
        }
        _ansi.WriteLine("");
    }

    // ── Header ────────────────────────────────────────────────────────────────

    void PrintHeader(string configPath, int instanceCount, string? filterRegion, string? filterService)
    {
        _ansi.WriteLine("");
        _ansi.MarkupLine("[grey]  +======================================+[/]");
        _ansi.MarkupLine("[grey]  |[/]  [bold]    Service Health Checker       [/][grey]|[/]");
        _ansi.MarkupLine("[grey]  +======================================+[/]");
        _ansi.MarkupLine($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  {instanceCount} instance(s)");
        _ansi.MarkupLine($"  config  : {Markup.Escape(configPath)}");
        if (filterRegion  is not null) _ansi.MarkupLine($"  region  : {Markup.Escape(filterRegion)}");
        if (filterService is not null) _ansi.MarkupLine($"  service : {Markup.Escape(filterService)}");
        _ansi.WriteLine("");
    }

    // ── HTTP check results ────────────────────────────────────────────────────

    void PrintHttpResults(IReadOnlyList<CheckEntry> entries, KibanaConfig? kibana, int? globalWarnMs = null, bool argoTokenMissing = false)
    {
        var grouped = entries
            .GroupBy(x => (x.Region, x.Svc.CatalogName))
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            var first = group.First();
            _ansi.MarkupLine($"  [aqua][[{Markup.Escape(first.Region)}]]  {Markup.Escape(first.Svc.CatalogName)}[/]");

            var orderedEntries = group.OrderBy(x => x.Result.Url).ToList();
            for (int i = 0; i < orderedEntries.Count; i++)
            {
                var entry     = orderedEntries[i];
                var r         = entry.Result;
                var threshold = entry.Svc.ResponseTimeWarnMs ?? globalWarnMs;
                var isSlow    = threshold.HasValue && r.Healthy && r.ElapsedMs > threshold.Value;

                if (r.Healthy)
                {
                    var indicator = isSlow ? "[yellow]WN[/]" : "[green]OK[/]";
                    _ansi.MarkupLine($"    {indicator}  {r.ElapsedMs,5}ms  {Link(r.Url)}");
                }
                else
                {
                    _ansi.MarkupLine($"[red]    !!    ---    [/]{Link(r.Url)}");

                    if (r.Error is not null)
                    {
                        _ansi.MarkupLine($"[maroon]               -> {Markup.Escape(r.Error)}[/]");

                        if (IsDnsError(r.Error))
                        {
                            var rcForHint = entry.Svc.Regions.TryGetValue(entry.Region, out var rcVal) ? rcVal : null;
                            var isK8s     = rcForHint?.KubernetesCluster is not null || rcForHint?.KubernetesUrl is not null;

                            if (isK8s && argoTokenMissing)
                            {
                                _ansi.MarkupLine("[grey]               -> Hint: this URL was constructed from catalog data because no ArgoCD token is available.[/]");
                                _ansi.MarkupLine("[grey]                         With a valid token the real ingress URL is resolved automatically.[/]");
                                _ansi.MarkupLine("[grey]                         Refresh your token: dotnet run -- set-token <jwt>[/]");
                            }
                            else if (!isK8s)
                            {
                                _ansi.MarkupLine("[grey]               -> Hint: host not resolved - URL pattern may be wrong.[/]");
                                _ansi.MarkupLine("[grey]                         If this service uses a shorter DNS format, set vm_url_template in the region config.[/]");
                                _ansi.MarkupLine("[grey]                         e.g. \"vm_url_template\": \"http://{target}.{env}-{domain}:{port}/\"[/]");
                            }
                        }
                    }

                    if (kibana is not null)
                    {
                        var regionId     = entry.Svc.Regions.TryGetValue(entry.Region, out var rc)
                                           ? rc.KibanaDataViewId : null;
                        var instanceName = ExtractVmInstanceName(r.Url);
                        var kibanaUrl    = BuildKibanaUrl(kibana, entry.Svc.CatalogName,
                                                          regionId, entry.Svc.KibanaDataViewId, instanceName);
                        if (kibanaUrl is not null)
                        {
                            _ansi.Markup("[olive]               -> Logs: [/]");
                            _rawOut.WriteLine(RawLink(kibanaUrl));
                        }
                        else
                            _ansi.MarkupLine("[olive]               -> Logs: no kibana_data_view_id configured for this service[/]");
                    }

                    // Blank line between consecutive failed entries for readability.
                    if (i < orderedEntries.Count - 1)
                        _ansi.WriteLine("");
                }
            }
            _ansi.WriteLine("");
        }
    }

    // ── Token warnings ────────────────────────────────────────────────────────

    void PrintTokenMessages(string? warning, string? noTokenMessage)
    {
        if (warning is not null)
        {
            var color = warning.Contains("expired") ? "red" : "yellow";
            _ansi.MarkupLine($"  [{color}]{Markup.Escape(warning)}[/]");
            _ansi.WriteLine("");
        }

        if (noTokenMessage is not null)
        {
            foreach (var line in noTokenMessage.Split('\n'))
                _ansi.MarkupLine($"  [yellow]{Markup.Escape(line)}[/]");
            _ansi.WriteLine("");
        }
    }

    // ── ArgoCD results ────────────────────────────────────────────────────────

    void PrintArgocdResults(IReadOnlyList<ArgocdEntry> entries)
    {
        _ansi.MarkupLine("[grey]  ── ArgoCD Pod Health ──────────────────[/]");
        _ansi.WriteLine("");

        foreach (var e in entries)
        {
            var podLabel   = e.PodCount > 0 ? $"{e.PodCount} pod(s)" : "? pods";
            var statusColor = e.Status switch
            {
                "Healthy"     => "green",
                "Progressing" => "yellow",
                _             => "red",
            };

            _ansi.Markup($"  [aqua][[{Markup.Escape(e.Region)}]]  {Markup.Escape(e.SvcName)}[/]");
            _ansi.Markup($"  ({Markup.Escape(e.AppName)} · {podLabel})  ");
            _ansi.MarkupLine($"[{statusColor}]{Markup.Escape(e.Status)}[/]");

            foreach (var d in e.DegradedResources)
                _ansi.MarkupLine($"[maroon]    └─ {Markup.Escape(d)}[/]");

            if (e.Status != "Healthy")
                _ansi.MarkupLine($"[olive]    → Investigate: [/]{Link(e.ArgoUrl)}");
        }

        _ansi.WriteLine("");
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    void PrintSummary(int healthy, int total, int slow = 0, int? slowThresholdMs = null)
    {
        var failed   = total - healthy;
        var slowNote = slow > 0 && slowThresholdMs.HasValue
            ? $"   ({slow} slow: >{slowThresholdMs}ms)"
            : "";

        _ansi.MarkupLine("  [grey]" + new string('-', 40) + "[/]");

        var color = failed == 0     ? "green"
                  : failed == total ? "red"
                                    : "yellow";
        var text = failed == 0
            ? $"All {healthy} instance(s) healthy{slowNote}"
            : $"{healthy} / {total} healthy   ({failed} failed){slowNote}";

        _ansi.MarkupLine($"  [{color}]{Markup.Escape(text)}[/]");
        _ansi.WriteLine("");
    }

    // ── Watch mode helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Prints a horizontal separator line between watch-mode runs (run 2 onwards).
    /// Called after the countdown bar erases itself, before the change banner.
    /// </summary>
    internal static void PrintWatchSeparator(int run, bool noColor)
    {
        var ansi  = MakeAnsi(noColor);
        var ts    = DateTime.Now.ToString("HH:mm:ss");
        var label = $"Run #{run} · {ts}";
        ansi.Write(new Rule($"[grey]{Markup.Escape(label)}[/]").RuleStyle("grey").LeftJustified());
        ansi.WriteLine("");
    }

    /// <summary>
    /// Prints a diff banner comparing the current run's HTTP results against the previous run.
    /// Called between watch-mode iterations to highlight status changes at a glance.
    /// Pass null for <paramref name="prev"/> on the first run (banner is suppressed).
    /// </summary>
    internal static void PrintChangeBanner(
        IReadOnlyList<CheckEntry>? prev,
        IReadOnlyList<CheckEntry>  curr,
        bool noColor)
    {
        if (prev is null) return;

        var ansi = MakeAnsi(noColor);

        var prevMap = prev.ToDictionary(
            e => $"{e.Svc.CatalogName}|{e.Region}|{e.Result.Url}",
            e => e.Result.Healthy);

        var changes = curr
            .Where(e => prevMap.TryGetValue(
                $"{e.Svc.CatalogName}|{e.Region}|{e.Result.Url}", out var was) &&
                was != e.Result.Healthy)
            .ToList();

        if (changes.Count == 0)
        {
            ansi.MarkupLine("[grey]  No status changes since last run.[/]");
            ansi.WriteLine("");
            return;
        }

        ansi.MarkupLine($"[aqua]  {changes.Count} change(s) since last run:[/]");

        foreach (var e in changes)
        {
            var arrow = e.Result.Healthy ? "↑" : "↓";
            var color = e.Result.Healthy ? "green" : "red";
            ansi.MarkupLine($"  [{color}]{arrow}  {Markup.Escape(e.Svc.CatalogName)} [[{Markup.Escape(e.Region)}]]  {Markup.Escape(e.Result.Url)}[/]");
        }
        ansi.WriteLine("");
    }

    /// <summary>
    /// Renders a live countdown bar after a watch-mode report.
    /// Uses Console.ForegroundColor directly (not Spectre) because the \r in-place
    /// overwrite technique is incompatible with Spectre's rendering model.
    /// </summary>
    internal static async Task RunCountdownAsync(int run, int intervalSec, CancellationToken ct, bool noColor)
    {
        const int barWidth  = 25;
        const int lineWidth = 70;

        var deadline = DateTime.Now.AddSeconds(intervalSec);

        Console.WriteLine();

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var remaining = Math.Max(0, (int)(deadline - DateTime.Now).TotalSeconds);
                var elapsed   = intervalSec - remaining;
                var filled    = Math.Clamp((int)((double)elapsed / intervalSec * barWidth), 0, barWidth);
                var bar       = new string('█', filled) + new string('░', barWidth - filled);

                if (!noColor) Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"\r  Run #{run}  [{bar}]  {remaining,2}s  ·  Ctrl+C to stop");
                if (!noColor) Console.ResetColor();

                if (remaining == 0) break;

                await Task.Delay(1_000, ct);
            }
        }
        finally
        {
            Console.Write($"\r{new string(' ', lineWidth)}\r");
        }
    }

    // ── Kibana URL builder ────────────────────────────────────────────────────

    /// <summary>Resolution order: region → service → global. Returns null when no ID is found.</summary>
    /// <param name="instanceName">
    /// When non-null (VM checks only), appends <c>AND host.name: "…"</c> to scope logs to that instance.
    /// </param>
    internal static string? BuildKibanaUrl(
        KibanaConfig kibana, string catalogName,
        string? regionId, string? serviceId,
        string? instanceName = null)
    {
        var dataViewId = regionId ?? serviceId ?? kibana.DataViewId;
        if (dataViewId is null) return null;

        var query = kibana.QueryTemplate.Replace("{service}", catalogName);
        if (instanceName is not null)
            query += $" AND host.name: \"{instanceName}\"";

        var escapedQuery = query.Replace("'", "%27");

        // Build the RISON columns segment. Empty → columns:!() (Kibana default view).
        // Populated → columns:!(col1,col2,...) which pre-selects the Discover table columns.
        var columnsRison = kibana.Columns is { Length: > 0 }
            ? $"columns:!({string.Join(",", kibana.Columns)})"
            : "columns:!()";

        return $"{kibana.Url.TrimEnd('/')}/app/discover#/?" +
               $"_g=(filters:!(),refreshInterval:(pause:!t,value:60000),time:(from:now-{kibana.LogMinutes}m,to:now))" +
               $"&_a=({columnsRison},dataSource:(dataViewId:'{dataViewId}',type:dataView)," +
               $"filters:!(),hideChart:!f,interval:auto," +
               $"query:(language:kuery,query:'{escapedQuery}')," +
               $"sort:!(!('@timestamp',desc)))";
    }

    // ── Instance name extraction ──────────────────────────────────────────────

    /// <summary>
    /// Extracts the VM instance name from a URL if the check is against a VM target.
    /// VM URLs follow the pattern: http://service-name.{vm-target}.platform-region.env-domain.tld:port/
    /// VM targets are identified by having a numeric last dash-segment (same rule as CatalogClient).
    /// Returns null for k8s URLs or any URL that doesn't match the pattern.
    /// </summary>
    internal static string? ExtractVmInstanceName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var segments = uri.Host.Split('.');
        if (segments.Length < 2) return null;

        var candidate = segments[1];
        return int.TryParse(candidate.Split('-').Last(), out _) ? candidate : null;
    }

    // ── DNS error detection ───────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the error message suggests the host could not be resolved -
    /// a strong signal that the VM URL pattern may be wrong and vm_url_template is needed.
    /// </summary>
    internal static bool IsDnsError(string error) =>
        error.Contains("No such host",              StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("nodename nor servname",      StringComparison.OrdinalIgnoreCase) ||
        error.Contains("host not found",             StringComparison.OrdinalIgnoreCase);
}
