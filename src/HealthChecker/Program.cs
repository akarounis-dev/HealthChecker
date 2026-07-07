using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using HealthChecker.Application;
using HealthChecker.Models;
using HealthChecker.Output;

// ── Options ───────────────────────────────────────────────────────────────────

var envOpt     = new Option<string?>("--env",     "Config environment: dev | stg | prod");
var configOpt  = new Option<string?>("--config",  "Explicit config file path; overrides --env");
var regionOpt  = new Option<string?>("--region",  "Filter to a single region");
var serviceOpt = new Option<string?>("--service", "Filter to a single service (alias or catalog name)");
var versionOpt = new Option<string?>("--catalog-version", "Pin a catalog version (default: latest deployed)");
var noArgoOpt  = new Option<bool>   ("--no-argocd", "Skip ArgoCD pod-level checks");
var noColorOpt = new Option<bool>   ("--no-color",  "Disable ANSI colour output (also NO_COLOR env var)");
var jsonOpt    = new Option<bool>   ("--json",       "Output results as JSON instead of the formatted table");
var listOpt    = new Option<bool>   ("--list",       "Print configured services and regions then exit");

// --watch[=N]: present without value → 30s default; present with value → use that; absent → no watch
var watchOpt = new Option<int?>("--watch", "Re-run every N seconds (default 30); Ctrl+C to stop")
{
    Arity = ArgumentArity.ZeroOrOne,
};

// --mock-fail[=regions], --mock-argo-fail[=regions]: bare flag means wildcard "*"
var mockFailOpt     = new Option<string?>("--mock-fail",      "Simulate HTTP failures (bare flag = all regions)")
    { Arity = ArgumentArity.ZeroOrOne };
var mockArgoFailOpt = new Option<string?>("--mock-argo-fail", "Simulate ArgoCD degraded (bare flag = all regions)")
    { Arity = ArgumentArity.ZeroOrOne };

// ── Root command ──────────────────────────────────────────────────────────────

var root = new RootCommand("Service health checker - checks HTTP endpoints and ArgoCD pod health.");

root.AddGlobalOption(envOpt);
root.AddGlobalOption(configOpt);

root.AddOption(regionOpt);
root.AddOption(serviceOpt);
root.AddOption(versionOpt);
root.AddOption(noArgoOpt);
root.AddOption(noColorOpt);
root.AddOption(jsonOpt);
root.AddOption(listOpt);
root.AddOption(watchOpt);
root.AddOption(mockFailOpt);
root.AddOption(mockArgoFailOpt);

root.SetHandler(async (InvocationContext ctx) =>
{
    var env        = ctx.ParseResult.GetValueForOption(envOpt);
    var configArg  = ctx.ParseResult.GetValueForOption(configOpt);
    var region     = ctx.ParseResult.GetValueForOption(regionOpt);
    var service    = ctx.ParseResult.GetValueForOption(serviceOpt);
    var version    = ctx.ParseResult.GetValueForOption(versionOpt);
    var noArgocd   = ctx.ParseResult.GetValueForOption(noArgoOpt);
    var json       = ctx.ParseResult.GetValueForOption(jsonOpt);
    var list       = ctx.ParseResult.GetValueForOption(listOpt);
    var mockFail   = ParseMockFlag(ctx, mockFailOpt);
    var mockArgo   = ParseMockFlag(ctx, mockArgoFailOpt);

    // --no-color: explicit flag OR NO_COLOR env var
    var noColor = ctx.ParseResult.GetValueForOption(noColorOpt) ||
                  !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    // --watch: check if the option was explicitly provided (regardless of value)
    bool watchMode    = ctx.ParseResult.FindResultFor(watchOpt) is not null;
    int? watchValue   = ctx.ParseResult.GetValueForOption(watchOpt);
    int  intervalSec  = watchValue is > 0 ? watchValue.Value : 30;

    var configPath = configArg
                  ?? (env is not null ? $"configs/config.{env}.json" : "configs/config.json");

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"Config file not found: {configPath}");
        ctx.ExitCode = 1;
        return;
    }

    Config config;
    try
    {
        var rawJson = await File.ReadAllTextAsync(configPath);
        var opts    = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        config      = JsonSerializer.Deserialize<Config>(rawJson, opts)
                      ?? throw new InvalidOperationException("Deserialized config was null.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to parse config: {ex.Message}");
        ctx.ExitCode = 1;
        return;
    }

    var validationErrors = ConfigValidator.Validate(config);
    if (validationErrors.Count > 0)
    {
        foreach (var e in validationErrors)
            Console.Error.WriteLine($"Config error: {e}");
        ctx.ExitCode = 1;
        return;
    }

    // ── --list ─────────────────────────────────────────────────────────────────

    if (list)
    {
        Console.WriteLine();
        Console.WriteLine($"  Services in {configPath}:");
        Console.WriteLine();

        foreach (var svc in config.Services)
        {
            var aliasNote = svc.Alias is not null ? $"  (alias: {svc.Alias})" : "";
            var warnNote  = svc.ResponseTimeWarnMs.HasValue ? $"  warn>{svc.ResponseTimeWarnMs}ms" : "";
            Console.WriteLine($"  {svc.CatalogName}{aliasNote}  port {svc.Port}{warnNote}");

            foreach (var (rgn, rc) in svc.Regions.OrderBy(x => x.Key))
            {
                var parts = new List<string>();
                if (rc.CatalogPlatform is not null) parts.Add($"catalog [{rc.CatalogPlatform}/{rc.CatalogRegion}]");
                if (rc.VmTargets?.Length > 0)        parts.Add($"VM ({rc.VmTargets.Length} hardcoded)");
                if (rc.KubernetesUrl  is not null)   parts.Add("k8s (hardcoded URL)");
                var method = parts.Count > 0 ? string.Join(" · ", parts) : "no resolution method";
                Console.WriteLine($"    {rgn,-14}  {method}");
            }
            Console.WriteLine();
        }

        if (config.Defaults?.ResponseTimeWarnMs.HasValue == true)
            Console.WriteLine($"  Global response_time_warn_ms: {config.Defaults.ResponseTimeWarnMs}ms");

        ctx.ExitCode = 0;
        return;
    }

    // ── Run options ────────────────────────────────────────────────────────────

    var runOptions = new RunOptions(
        ConfigPath:          configPath,
        FilterRegion:        region,
        FilterService:       service,
        CatalogVersion:      version,
        SkipArgocd:          noArgocd,
        MockFailRegions:     mockFail,
        MockArgoFailRegions: mockArgo
    );

    IReporter reporter = json ? new JsonReporter() : new ConsoleReporter(noColor);

    // ── --watch mode ───────────────────────────────────────────────────────────

    if (watchMode)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        IReadOnlyList<CheckEntry>? prevResults = null;
        HealthCheckReport?         lastReport  = null;
        int run = 0;

        while (!cts.IsCancellationRequested)
        {
            run++;
            var runnerTask = new HealthCheckRunner(config, runOptions).RunAsync();
            var report     = json
                ? await runnerTask
                : await Spinner.RunAsync(runnerTask);

            // Compute status diff (null on run 1 - no prior run to compare against).
            IReadOnlyList<WatchStatusChange>? watchChanges = null;
            if (prevResults is not null)
            {
                var prevMap = prevResults.ToDictionary(
                    e => $"{e.Svc.CatalogName}|{e.Region}|{e.Result.Url}",
                    e => e.Result.Healthy);
                watchChanges = report.HttpResults
                    .Where(e => prevMap.TryGetValue(
                        $"{e.Svc.CatalogName}|{e.Region}|{e.Result.Url}", out var was) &&
                        was != e.Result.Healthy)
                    .Select(e => new WatchStatusChange(
                        e.Svc.CatalogName, e.Region, e.Result.Url, e.Result.Healthy))
                    .ToList();
            }

            var watchReport = report with
            {
                // Show catalog events on run 1 so failures are visible; suppress on re-runs
                // (targets are already resolved and events would be redundant noise).
                CatalogEvents = run == 1 ? report.CatalogEvents : [],
                WatchRun      = run,
                WatchChanges  = watchChanges,
            };

            if (!json)
            {
                if (run > 1)
                {
                    Console.WriteLine();
                    ConsoleReporter.PrintWatchSeparator(run, noColor);
                }
                ConsoleReporter.PrintChangeBanner(prevResults, report.HttpResults, noColor);
            }

            reporter.Report(watchReport);

            prevResults = report.HttpResults;
            lastReport  = report;

            if (!json)
            {
                try { await ConsoleReporter.RunCountdownAsync(run, intervalSec, cts.Token, noColor); }
                catch (OperationCanceledException) { break; }
            }
            else
            {
                try { await Task.Delay(intervalSec * 1_000, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        ctx.ExitCode = lastReport?.ExitCode ?? 0;
        return;
    }

    // ── Single run ─────────────────────────────────────────────────────────────

    var singleTask   = new HealthCheckRunner(config, runOptions).RunAsync();
    var singleReport = json
        ? await singleTask
        : await Spinner.RunAsync(singleTask);

    reporter.Report(singleReport);
    ctx.ExitCode = singleReport.ExitCode;
});

// ── set-token subcommand ──────────────────────────────────────────────────────

var setTokenCmd = new Command("set-token", "Write an ArgoCD JWT into config file(s)");
var jwtArg      = new Argument<string>("jwt", "The JWT token to write");
setTokenCmd.AddArgument(jwtArg);

setTokenCmd.SetHandler(async (InvocationContext ctx) =>
{
    var jwt       = ctx.ParseResult.GetValueForArgument(jwtArg);
    var targetEnv = ctx.ParseResult.GetValueForOption(envOpt);
    var targetCfg = ctx.ParseResult.GetValueForOption(configOpt);

    string[] files;

    if (targetCfg is not null)
    {
        if (!File.Exists(targetCfg))
        {
            Console.Error.WriteLine($"Config file not found: {targetCfg}");
            ctx.ExitCode = 1;
            return;
        }
        files = [targetCfg];
    }
    else if (targetEnv is not null)
    {
        var path = $"configs/config.{targetEnv}.json";
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Config file not found: {path}");
            ctx.ExitCode = 1;
            return;
        }
        files = [path];
    }
    else
    {
        const string configDir = "configs";
        if (!Directory.Exists(configDir))
        {
            Console.Error.WriteLine($"Directory not found: {configDir}");
            ctx.ExitCode = 1;
            return;
        }

        files = Directory.GetFiles(configDir, "config.*.json")
            .Where(f => !Path.GetFileName(f).Equals("config.sample.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            Console.Error.WriteLine("No config files found (configs/config.*.json, excluding sample).");
            ctx.ExitCode = 1;
            return;
        }
    }

    foreach (var file in files)
    {
        var lines    = await File.ReadAllLinesAsync(file);
        bool updated = false;

        var newLines = lines.Select(line =>
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("\"argocd_token\""))
            {
                var indent = line[..(line.Length - trimmed.Length)];
                updated = true;
                return $"{indent}\"argocd_token\": \"{jwt}\"";
            }
            return line;
        }).ToArray();

        if (updated)
        {
            await File.WriteAllTextAsync(file, string.Join(Environment.NewLine, newLines) + Environment.NewLine);
            Console.WriteLine($"  Updated : {file}");
        }
        else
        {
            Console.WriteLine($"  Skipped : {file}  (no argocd_token field found)");
        }
    }

    Console.WriteLine("Done.");
});

root.AddCommand(setTokenCmd);

// ── Invoke ────────────────────────────────────────────────────────────────────

return await root.InvokeAsync(args);

// ── Helpers ───────────────────────────────────────────────────────────────────

// Parses a --mock-* option: absent → empty set, bare flag → {"*"}, =val → split on comma
static HashSet<string> ParseMockFlag(InvocationContext ctx, Option<string?> opt)
{
    if (ctx.ParseResult.FindResultFor(opt) is null) return [];
    var value = ctx.ParseResult.GetValueForOption(opt);
    var raw   = value ?? "*";
    return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
              .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
