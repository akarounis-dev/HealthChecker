# HealthChecker

A CLI tool that runs a multi-phase health check across your services:

1. **Catalog resolution** — looks up live deployment targets (VMs and k8s clusters) from ServiceKatalog.
2. **HTTP health checks** — GETs each target and checks the response body for an expected string.
3. **ArgoCD pod health** — queries the ArgoCD API for app status and pod count per k8s region.

Failed checks print a Kibana link scoped to that service and instance so you can jump straight to logs.

---

## Requirements

- .NET 8 SDK
- Access to ServiceKatalog, ArgoCD, and Kibana (network-level)

---

## Setup

```bash
git clone <repo>
cd HealthChecker
cp src/HealthChecker/configs/config.sample.json src/HealthChecker/configs/config.prod.json
# edit config.prod.json as needed
dotnet build
```

---

## Running

All `dotnet run` commands must be run from `src/HealthChecker/`.

```bash
# Check all services in the prod environment
dotnet run -- --env=prod

# Check a single service (matched by alias or catalog name)
dotnet run -- --env=prod --service=feed

# Filter to a specific region
dotnet run -- --env=prod --region=europe

# Skip ArgoCD checks (faster, no token needed)
dotnet run -- --env=prod --no-argocd

# Use an explicit config path instead of --env
dotnet run -- --config=configs/config.prod.json

# Disable colour output (also honoured via NO_COLOR env var)
dotnet run -- --env=prod --no-color
```

### List configured services (no checks run)

`--list` prints every service and region defined in the config, including how each region resolves its targets, then exits immediately without hitting any endpoints.

```bash
dotnet run -- --env=prod --list
```

### Watch mode — continuous polling

`--watch` re-runs the full check on a loop. Each run appends below the previous one — the screen is never cleared — so you can scroll back to read any earlier run in full. A separator line marks the start of each new run:

```
  ─── Run #2 · 15:03:35 ──────────────────────────────────────────────
```

After the first run, a diff banner shows any services that flipped status (↑ recovered, ↓ newly failed) since the previous run. A live countdown bar shows time until the next run; press Ctrl+C to stop cleanly.

```bash
# Poll every 30 seconds (default)
dotnet run -- --env=prod --watch

# Poll every 60 seconds
dotnet run -- --env=prod --watch=60

# Watch a single service only
dotnet run -- --env=prod --service=feed --watch=30
```

### Output as JSON

`--json` writes a machine-readable JSON payload to stdout instead of the formatted table. The spinner and all human-readable chrome are suppressed so the output can be piped cleanly.

```bash
dotnet run -- --env=prod --json
dotnet run -- --env=prod --json | jq '.exit_code'
dotnet run -- --env=prod --json | jq '.summary'
```

Combining `--json` with `--watch` produces a newline-delimited stream of JSON objects, one per run. Each object includes two additional fields:

- `watch_run` — integer run index (1, 2, 3, …)
- `watch_changes` — `null` on run 1 (no baseline yet); an array on subsequent runs, empty when nothing changed, or a list of URLs that flipped status

```bash
dotnet run -- --env=prod --json --watch | jq 'select(.watch_changes | length > 0)'
dotnet run -- --env=prod --json --watch | jq '{run: .watch_run, failed: .summary.failed, changes: .watch_changes}'
```

Each entry in `watch_changes` has `service`, `region`, `url`, and `healthy` (`true` = recovered, `false` = newly failed).

### Update the ArgoCD token

```bash
# Update all non-sample config files at once
dotnet run -- set-token eyJhbGci...

# Update a specific environment only
dotnet run -- set-token eyJhbGci... --env=prod

# Update an arbitrary config file
dotnet run -- set-token eyJhbGci... --config=configs/config.prod.json
```

### Simulating failures (useful for testing output)

```bash
# Simulate all HTTP checks failing
dotnet run -- --env=dev --mock-fail

# Simulate HTTP failures for a specific region only
dotnet run -- --env=dev --mock-fail=europe

# Simulate ArgoCD degraded for all regions
dotnet run -- --env=dev --mock-argo-fail=*

# Simulate everything failing
dotnet run -- --env=dev --mock-fail=* --mock-argo-fail=*
```

---

## Output indicators

| Indicator | Colour | Meaning |
|---|---|---|
| `OK` | Green | Healthy, within response time threshold |
| `WN` | Yellow | Healthy, but slower than `response_time_warn_ms` |
| `!!` | Red | Failed (wrong body, timeout, connection error, etc.) |

The summary line shows a slow count when any `WN` checks exist, e.g. `All 5 instance(s) healthy   (2 slow: >500ms)`.

---

## Exit codes

Exit codes are bit-flags and can be OR'd together. Use bitwise AND in CI to test for specific failure types.

| Bit | Value | Meaning |
|---|---|---|
| — | 0 | All checks healthy |
| 0 | 1 | One or more HTTP checks failed |
| 1 | 2 | One or more ArgoCD apps not Healthy (excludes Unknown) |
| 0+1 | 3 | HTTP checks failed **and** ArgoCD degraded |
| 2 | 4 | One or more catalog lookups failed |

Values 5–7 combine catalog failure (4) with HTTP/ArgoCD bits. For example, exit code 5 (4 | 1) means some catalog lookups failed AND some HTTP checks on successful regions also failed.

`WN` (slow) checks do **not** affect the exit code — they are warnings only.

Process errors (bad config path, parse failure, validation error) exit with code `1`.

---

## Config file

Config files live in `src/HealthChecker/configs/` and are selected by `--env=<name>`, mapping to `config.<name>.json`.

`config.sample.json` contains real default values for all fields except `argocd_token`. Copy it when setting up a new environment.

### Top-level structure

```jsonc
{
  "defaults": { ... },   // optional — global defaults for timeouts, retries, thresholds
  "kibana":   { ... },
  "catalog":  { ... },
  "services": [ ... ]
}
```

### `defaults`

All fields are optional. Per-service values override these.

| Field | Default | Description |
|---|---|---|
| `timeout_seconds` | `5` | Per-attempt HTTP timeout |
| `retry_attempts` | `2` | Number of retries on failure |
| `retry_delay_ms` | `1000` | Delay between retries |
| `response_time_warn_ms` | `null` | Healthy checks slower than this render as `WN` (yellow). `null` disables the threshold. |

### `kibana`

| Field | Default | Description |
|---|---|---|
| `url` | — | Base URL of your Kibana instance |
| `data_view_id` | `null` | Global fallback data view ID (can be overridden per service or per region) |
| `log_minutes` | `5` | Time window for log links, e.g. `5` → last 5 minutes |
| `query_template` | `level: "Error"` | KQL query; `{service}` is replaced with the catalog name at runtime |
| `columns` | `null` | Kibana Discover columns to pre-select in the generated link. When set, the link opens with a readable table instead of the default dense view. Example: `["level", "host.name", "message", "logger", "exceptionType", "exceptionMessage"]` |

### `catalog`

| Field | Description |
|---|---|
| `base_url` | ServiceKatalog REST API base |
| `environment` | Environment passed to the catalog API and used in ArgoCD app name construction (`dev`, `stg`, `prd`) |
| `k8s_domain` | Domain suffix used when constructing k8s ingress and VM URLs |
| `argocd_server` | ArgoCD server base URL |
| `argocd_token` | JWT for ArgoCD. Set to `null` and supply via `ARGOCD_TOKEN` env var instead (see below) |

### `services[]`

| Field | Description |
|---|---|
| `catalog_name` | Name registered in ServiceKatalog (used in catalog queries, ArgoCD app names, and URL construction) |
| `alias` | Short name for `--service` filtering. Falls back to `catalog_name` when not set |
| `port` | Port used when building VM target URLs |
| `healthcheck_contains` | String that must appear in the response body for the check to pass |
| `retry_attempts` | Overrides `defaults.retry_attempts` for this service |
| `timeout_seconds` | Overrides `defaults.timeout_seconds` for this service |
| `response_time_warn_ms` | Overrides `defaults.response_time_warn_ms` for this service |
| `kibana_data_view_id` | Service-level Kibana data view ID (overrides global, overridden by region-level) |
| `regions` | Map of region name → region config (see below) |

### `regions` (per service)

| Field | Description |
|---|---|
| `catalog_platform` | Platform code passed to catalog API (e.g. `eur`, `cen`, `bra`) |
| `catalog_region` | Region code passed to catalog API (e.g. `euw`, `eun`, `brs`, `sec`) |
| `kibana_data_view_id` | Region-level Kibana data view ID (highest priority) |
| `vm_targets` | Hardcoded list of VM target FQDNs (skips catalog lookup if set) |
| `kubernetes_url` | Hardcoded k8s ingress URL (skips catalog lookup if set) |
| `vm_url_template` | Custom URL pattern for VM targets. Placeholders: `{service}` `{target}` `{platform}` `{region}` `{env}` `{domain}` `{port}`. Default: `http://{service}.{target}.{platform}-{region}.{env}-{domain}:{port}/` |

---

## ArgoCD token

### Retrieving a token

1. Open the ArgoCD web UI at `https://argo.k8s.dvo-novibet.systems` and log in via SSO.
2. Open browser DevTools → **Network** tab.
3. Navigate anywhere inside the UI to trigger an API call, then find any request to `/api/v1/`.
4. Open that request → **Headers** → **Request Headers** → find the `Cookie` header → copy the value after `argocd.token=`.
5. Write it into all your config files at once:

```bash
dotnet run -- set-token eyJhbGci...
```

### Token resolution priority

The tool checks these sources in order, using the first non-empty value:

1. `ARGOCD_TOKEN` environment variable
2. `argocd_token` field in the config file
3. `~/.config/argocd/config` (written by `argocd login`)

The tool warns when the token is within 24 hours of expiry, when it has already expired, or when it is not a valid JWT (e.g. a partially copied value). In all three cases ArgoCD checks are skipped and the warning is printed below the results table.

If an ArgoCD token is unavailable and a k8s service fails with a DNS error, the tool prints a hint explaining that the URL was constructed from catalog data and that refreshing the token would resolve the correct ingress URL automatically.

---

## Adding a new service

1. Find the service's `catalog_name` in ServiceKatalog.
2. Add an entry to `services[]` in your config:

```jsonc
{
  "catalog_name": "web-myservice",
  "alias": "mysvc",
  "port": 8080,
  "healthcheck_contains": "MyService is live!",
  "regions": {
    "europe": {
      "catalog_platform": "eur",
      "catalog_region":   "euw",
      "kibana_data_view_id": "<data-view-id>"
    }
  }
}
```

3. Run `dotnet run -- --env=dev --service=mysvc --mock-fail` to verify the config parses and output looks correct before running against real infrastructure.

If the HTTP check fails with a DNS error, the service likely uses a non-standard URL pattern. Set `vm_url_template` on the region to override:

```jsonc
"vm_url_template": "http://{target}.{env}-{domain}:{port}/"
```

Available placeholders: `{service}` `{target}` `{platform}` `{region}` `{env}` `{domain}` `{port}`.

---

## Tests

```bash
# Unit tests only (fast, no network) — run from repo root
dotnet test --filter "Category!=Integration"

# Single test by name
dotnet test --filter "FullyQualifiedName~Returns_Healthy_When_Response_Contains_Expected_String"

# Integration tests (hits real dev infrastructure)
RUN_INTEGRATION_TESTS=1 HEALTHCHECKER_ARGOCD_TOKEN=<jwt> dotnet test --filter "Category=Integration"
```

Integration tests return early and show as **Passed** (not Skipped) when `RUN_INTEGRATION_TESTS` is not set — this is a limitation of xUnit v2, which has no built-in dynamic skip.
