# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build / Test / Run Commands

```bash
# Build (from repo root)
dotnet build

# Run against an environment (from src/HealthChecker/)
dotnet run -- --env=dev
dotnet run -- --env=prod --service=feed --region=europe

# Common flag combinations
dotnet run -- --env=dev --mock-fail=*                        # simulate all HTTP checks failing
dotnet run -- --env=dev --mock-fail=* --mock-argo-fail=*     # simulate everything failing
dotnet run -- --env=prod --no-argocd                         # skip ArgoCD pod checks
dotnet run -- --config=configs/config.prod.json              # explicit config path

# Run all unit tests
dotnet test --filter "Category!=Integration"

# Run a single test by name
dotnet test --filter "FullyQualifiedName~Returns_Healthy_When_Response_Contains_Expected_String"

# Run integration tests (requires RUN_INTEGRATION_TESTS=1 and optionally HEALTHCHECKER_ARGOCD_TOKEN)
RUN_INTEGRATION_TESTS=1 HEALTHCHECKER_ARGOCD_TOKEN=<jwt> dotnet test --filter "Category=Integration"
```

The main project (`HealthChecker.csproj`) targets `net8.0`. The test project lives under `HealthChecker.Tests/` as a sibling directory — it is not included in the main SDK glob because the implicit glob for `src/HealthChecker/` never reaches `src/HealthChecker.Tests/`. No explicit `Compile Remove` is needed. `InternalsVisibleTo` exposes `internal` members to the test assembly.

## NuGet Packages

Key packages and their version-specific constraints:

- **Spectre.Console 0.49.1** — `IAnsiConsole`, `AnsiConsoleSettings`, `ColorSystem`/`ColorSystemSupport`, `Markup.Escape`, `[link=url]text[/]` markup. Kibana RISON URLs contain characters that crash the Spectre markup parser (`!`, `(`, `)`), so Kibana links bypass Spectre entirely and are written through `_rawOut` (see ConsoleReporter below).
- **Polly v8** — `ResiliencePipelineBuilder`, `RetryStrategyOptions`. `MaxRetryAttempts` must be ≥ 1; **never add the retry strategy when `retries == 0`** — wrap it in `if (retries > 0)` before calling `builder.AddRetry(...)`.
- **System.CommandLine 2.0.0-beta4** — `RootCommand`, `Option<T>`, `ArgumentArity.ZeroOrOne`, `InvocationContext`. Auto-generates a `--version` flag that collides with the package's own output; rename any version-like flag to `--catalog-version` to avoid the clash.

## High-Level Architecture

The tool runs a three-phase pipeline, all orchestrated inside `HealthCheckRunner.RunAsync()`:

1. **Catalog resolution** (`Clients/CatalogClient.cs`): For each `(service, region)` pair that has `catalog_platform` / `catalog_region` set and no pre-resolved targets, a concurrent fan-out of HTTP calls hits the ServiceKatalog REST API. The API returns a list of versioned deployment entries; the runner picks the one with the most recent `event_time` for the matching `(env, platform, region)` triple. Targets with a numeric last dash-segment are VM instances; targets with a non-numeric last segment are Kubernetes cluster names. Results are applied back to the in-memory `svc.Regions` dictionary sequentially after `Task.WhenAll`.

2. **HTTP health checks** (`Checks/Checker.cs`): All resolved URLs (k8s ingress and/or each VM instance) are checked concurrently with `Task.WhenAll`. Each check GETs the URL and looks for `healthcheck_contains` (case-insensitive) anywhere in the response body. Retries and per-attempt timeouts are applied via Polly v8. Results land in a `ConcurrentBag<CheckEntry>`.

3. **ArgoCD pod health** (`Clients/ArgocdClient.cs`): For each k8s region, the runner queries `/api/v1/applications/{appName}` on the ArgoCD server. App name is composed as `{catalog_name}-{platform}-{region}-{environment}`. Pod count falls back to the resource-tree endpoint when pods are not included in `status.resources`. Both steps in this phase are also concurrent.

After all three phases, `BuildReport` assembles a `HealthCheckReport` record and returns it to `Program.cs`, which passes it straight to `ConsoleReporter.Report()`.

**Key types:**

| Type | File | Role |
|---|---|---|
| `Config` | `Models/Config/Config.cs` | Root deserialized from the JSON config file |
| `Defaults` | `Models/Config/Defaults.cs` | Global defaults for timeouts, retries, and `ResponseTimeWarnMs` |
| `ServiceConfig` | `Models/Config/ServiceConfig.cs` | One entry in `config.services[]`; holds `CatalogName`, `Port`, `HealthcheckContains`, per-region config, and `Alias`. `ServiceAlias` returns `Alias ?? CatalogName` and is matched against `--service`. |
| `RegionConfig` | `Models/Config/RegionConfig.cs` | Per-region settings; resolved targets (`VmTargets`, `KubernetesUrl`, `KubernetesCluster`) are written back at runtime |
| `KibanaConfig` | `Models/Config/KibanaConfig.cs` | Kibana section; `Columns` (optional `string[]`) carries pre-selected Discover columns that are rendered into the RISON URL as `columns:!(col1,col2,...)` |
| `RunOptions` | `Models/Report/RunOptions.cs` | CLI flags parsed in `Program.cs` and passed to `HealthCheckRunner` |
| `HealthCheckReport` | `Models/Report/HealthCheckReport.cs` | Everything a reporter needs; produced by `HealthCheckRunner`, consumed by `IReporter`. Also carries `WatchRun` / `WatchChanges` when running in `--watch` mode (set in `Program.cs` via `with` expression, not by `BuildReport`). |
| `WatchStatusChange` | `Models/Report/HealthCheckReport.cs` | A URL whose health status flipped since the previous watch run: `(Service, Region, Url, Healthy)`. `Healthy = true` = recovered; `false` = newly failed. |
| `CheckEntry` | `Models/Pipeline/CheckEntry.cs` | One HTTP result row: `(ServiceConfig, Region, CheckResult)` |
| `ArgocdEntry` | `Models/Pipeline/ArgocdEntry.cs` | One ArgoCD app result row |
| `CatalogEvent` | `Models/Pipeline/CatalogEvent.cs` | Outcome of one catalog lookup (success/failure summary shown before the main table) |

## Config File Conventions

Config files live in `configs/` and are selected by `--env`:

- `config.dev.json` — `catalog.environment = "dev"`; k8s regions use `catalog_region: "sec"`; `argocd_token` is populated with a real JWT for local use.
- `config.stg.json` — `catalog.environment = "stg"`; k8s regions use `catalog_region: "eun"`; `argocd_token` is populated.
- `config.prod.json` — `catalog.environment = "prd"`; regions use `catalog_region: "euw"` / `"brs"` / `"eun"`; `argocd_token` is populated.
- `config.sample.json` — same structure as prod but with `argocd_token: null`; safe to commit. Copy it when adding a new environment.

Key fields:

- `catalog.environment` — passed verbatim to the ServiceKatalog API and used in ArgoCD app name composition and VM URL construction.
- `catalog.k8s_domain` — domain suffix for constructed k8s ingress URLs and VM target URLs (e.g. `novibet.systems`).
- `catalog.argocd_server` — base URL of the ArgoCD server. Token resolution priority: `ARGOCD_TOKEN` env var → `argocd_token` in config → `~/.config/argocd/config` (written by `argocd login`).
- `regions[*].catalog_platform` / `catalog_region` — the platform and region codes passed to the catalog API. If both are omitted, the runner skips catalog resolution and expects `vm_targets` or `kubernetes_url` to be hardcoded.
- `regions[*].kibana_data_view_id` — per-region Kibana data view; overrides service-level `kibana_data_view_id` and global `kibana.data_view_id`.
- `kibana.columns` — optional string array of Kibana Discover column names to pre-select in generated log links (e.g. `["level", "host.name", "message", "logger", "exceptionType", "exceptionMessage"]`). When null or empty, the URL uses `columns:!()` (Kibana default). Configured in all non-sample configs.
- `services[*].alias` — short name matched against `--service`. Falls back to `catalog_name` when not set. Matched case-insensitively.

## Key Design Rules to Preserve

### IReporter / zero console output outside ConsoleReporter
`IReporter` (defined in `Output/IReporter.cs`) has a single method `void Report(HealthCheckReport report)`. `HealthCheckRunner` produces zero console output; all presentation is delegated to the `IReporter` implementation the caller passes in. `ConsoleReporter` is explicitly documented as "the only class in the codebase allowed to write to the console." Intentional exceptions: `Spinner.cs` (run animation); `Program.cs` watch-mode chrome — `ConsoleReporter.PrintWatchSeparator` and `ConsoleReporter.PrintChangeBanner` — which are guarded by `!args.Contains("--json")` so they never pollute the JSON stream. Do not add `Console.Write*` calls anywhere else.

### ConsoleReporter `_rawOut` field and constructor
`ConsoleReporter` has two constructors and a `_rawOut TextWriter` field:

```csharp
readonly IAnsiConsole _ansi;
readonly TextWriter   _rawOut;

public ConsoleReporter(bool noColor = false)
    : this(MakeAnsi(noColor), Console.Out) { }

public ConsoleReporter(IAnsiConsole ansi, TextWriter rawOut)
{
    _ansi   = ansi;
    _rawOut = rawOut;
}
```

Kibana URLs contain RISON characters (`!`, `(`, `)`) that crash the Spectre markup parser, so they bypass `_ansi` entirely and are written through `_rawOut.WriteLine(RawLink(kibanaUrl))`. `RawLink` emits OSC-8 terminal hyperlink sequences (`\x1b]8;;{url}\x1b\\{url}\x1b]8;;\x1b\\`) for clickable links; in no-colour mode it returns the plain URL. Tests inject a `StringWriter` as `rawOut` to capture this output — without the injection the Kibana URL is invisible to test assertions.

### Parallel execution pattern
Both the catalog resolution phase and the HTTP/ArgoCD check phases use `Task.WhenAll` over a pre-built list of tasks, not `Parallel.ForEach` or sequential loops. The pattern is: build a list of lightweight lambdas/tasks, fan out with `Task.WhenAll`, collect results into a local list, then apply mutations sequentially afterward. Adding new concurrent work should follow this pattern — not introduce shared mutable state written from inside tasks.

### Polly v8 retry strategy guard
`Checker.cs` builds its Polly pipeline with `ResiliencePipelineBuilder`. `RetryStrategyOptions.MaxRetryAttempts` must be ≥ 1, so the retry strategy must be added conditionally:

```csharp
var builder = new ResiliencePipelineBuilder();
if (retries > 0)
{
    builder.AddRetry(new RetryStrategyOptions { MaxRetryAttempts = retries, ... });
}
var pipeline = builder.AddTimeout(...).Build();
```

Never call `builder.AddRetry(...)` when `retries == 0`; it throws at pipeline construction time.

### ArgoCD token validation
`ArgocdClient.GetTokenTimeLeft(token)` returns `null` for any string that is not a valid JWT (e.g. a partial copy, an empty string, or a cookie containing additional metadata). The return value must be checked for null explicitly before any expiry comparisons — nullable comparisons to `TimeSpan` silently evaluate to false and will allow an invalid token to be passed to ArgoCD API calls:

```csharp
var timeLeft = ArgocdClient.GetTokenTimeLeft(argoToken);
if (timeLeft is null)
{
    tokenWarning = "ArgoCD token is not a valid JWT — check argocd_token in your config.";
    argoToken    = null;
}
else if (timeLeft <= TimeSpan.Zero) { /* expired */ }
else if (timeLeft <= TimeSpan.FromHours(24)) { /* expiring soon */ }
```

### TargetUrlBuilder — catalog section is optional when `vm_url_template` is set
The `catalog` section guard in `TargetUrlBuilder` must only fire when no `vm_url_template` is configured. A service with `vm_url_template` set does not need `catalog.environment` or `catalog.k8s_domain` to construct VM URLs:

```csharp
if (rc.VmUrlTemplate is null && (catalogEnvironment is null || kubernetesDomain is null))
    throw new InvalidOperationException("The 'catalog' section is required ...");
```

### DNS hint — k8s vs VM differentiation
`ConsoleReporter.PrintHttpResults` receives an `argoTokenMissing` flag (computed from `report.NoTokenMessage` and `report.TokenWarning`). When a failed entry has a DNS error, the hint shown depends on whether the target is a k8s region:

- **k8s + token missing/expired**: Explains the URL was constructed from catalog data (not from ArgoCD ingress lookup) and instructs the user to refresh the token with `dotnet run -- set-token <jwt>`.
- **VM (non-k8s)**: Explains the URL pattern may be wrong and suggests setting `vm_url_template`.

Detecting k8s: check `rcForHint?.KubernetesCluster is not null || rcForHint?.KubernetesUrl is not null` on the region config.

### Watch mode — catalog re-resolved every run
In watch mode, catalog resolution runs on every poll cycle so that mid-release version changes (new VM batch, k8s rollout) are captured immediately. Before each resolution phase, `HealthCheckRunner` resets `VmTargets`, `KubernetesUrl`, and `KubernetesCluster` on all regions that have `CatalogPlatform`/`CatalogRegion` set (hardcoded targets without those fields are left untouched). `CatalogEvents` are passed through on every run so the version label in the output always reflects the current deployment.

### Fixture-based tests
Unit tests for `CatalogClient` and `ArgocdClient` never inline JSON payloads. Fixtures live in `HealthChecker.Tests/Fixtures/` (under `argocd/` and `catalog/` subdirectories) and are loaded via the helper classes `ArgocdJson` and `CatalogJson` in `HealthChecker.Tests/Helpers/`. Constants in those helpers mirror values embedded in the fixture files — use the constants in assertions rather than duplicating raw strings. New tests that need HTTP responses should follow this fixture pattern.

### MockHttpMessageHandler
`HealthChecker.Tests/Helpers/MockHttpMessageHandler.cs` is the only HTTP test double in the project. It uses a queue: call `EnqueueResponse` / `EnqueueException` / `EnqueueDelay` in order, then pass the handler to `new HttpClient(handler)`. Use `handler.RemainingResponses` to assert all enqueued responses were consumed.

### BuildReport signature
`HealthCheckRunner.BuildReport` takes positional parameters `(List<CatalogEvent>, List<CheckEntry>, List<ArgocdEntry>, string? tokenWarning, string? noTokenMessage)` and maps them directly onto the `HealthCheckReport` record constructor. It exists purely to avoid repeating the full constructor call at two return sites. If new pipeline fields are added, add them to both `HealthCheckReport` (as named optional parameters) and the `BuildReport` wrapper. Watch-mode-only fields (`WatchRun`, `WatchChanges`) are intentionally excluded from `BuildReport` — they are set in `Program.cs` via a `with` expression after the report is built, because `HealthCheckRunner` has no knowledge of watch context.

### ExitCode
`HealthCheckReport.ExitCode` uses bit-flags that OR together: bit 0 (value 1) = HTTP check failure; bit 1 (value 2) = ArgoCD app not `"Healthy"` or `"Unknown"`; bit 2 (value 4) = catalog resolution failure. Bits combine freely — exit code 7 means all three conditions are true. `Program.cs` returns this value as the process exit code. Keep this derived property on the record rather than computing it in `ConsoleReporter`.

### Security constraint — JWT tokens in config files
JWT tokens (`argocd_token`) must always be `null` in committed config files. `config.sample.json` always has `argocd_token: null`; the `set-token` command explicitly skips it. Config files that contain real JWTs are gitignored via `configs/config.*.json` with a `!configs/config.sample.json` negation. These rules must never be violated.
