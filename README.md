# HealthChecker

Checks HTTP health endpoints and ArgoCD pod health across environments. Resolves live targets from ServiceKatalog, GETs each one, and links failed checks to Kibana.

## Requirements

- .NET 8 SDK
- Network access to ServiceKatalog, ArgoCD, and Kibana

## Setup

```bash
git clone <repo>
cd HealthChecker
cp src/HealthChecker/configs/config.sample.json src/HealthChecker/configs/config.prod.json
# edit config.prod.json
dotnet build
```

Run all commands from `src/HealthChecker/`.

## Usage

```bash
dotnet run -- --env=prod
dotnet run -- --env=prod --service=feed
dotnet run -- --env=prod --region=europe
dotnet run -- --env=prod --no-argocd
dotnet run -- --env=prod --no-color          # also honoured via NO_COLOR env var
dotnet run -- --env=prod --list              # print configured services and exit
dotnet run -- --env=prod --watch             # poll every 30s
dotnet run -- --env=prod --watch=60          # poll every 60s
dotnet run -- --env=prod --json              # machine-readable output
dotnet run -- --env=prod --json --watch      # newline-delimited JSON stream
dotnet run -- --config=configs/config.prod.json
```

`--watch` re-resolves catalog on every cycle, so mid-release changes (new VMs, k8s rollout) are picked up automatically.

`--json --watch` adds `watch_run` (run index) and `watch_changes` (URLs that flipped status) to each object.

### Simulate failures

```bash
dotnet run -- --env=dev --mock-fail          # all HTTP checks fail
dotnet run -- --env=dev --mock-fail=europe   # one region only
dotnet run -- --env=dev --mock-argo-fail=*   # ArgoCD degraded everywhere
```

### ArgoCD token

```bash
dotnet run -- set-token eyJhbGci...          # update all configs
dotnet run -- set-token eyJhbGci... --env=prod
```

Token resolution order: `ARGOCD_TOKEN` env var → `argocd_token` in config → `~/.config/argocd/config`.

To get a token: log into the ArgoCD UI, open DevTools → Network, find any `/api/v1/` request, copy `argocd.token=` from the Cookie header.

## Output

| Indicator | Meaning |
|---|---|
| `OK` | Healthy |
| `WN` | Healthy but slower than `response_time_warn_ms` |
| `!!` | Failed |

## Exit codes

Bit-flags — OR'd together when multiple conditions apply.

| Value | Meaning |
|---|---|
| 0 | All healthy |
| 1 | HTTP check(s) failed |
| 2 | ArgoCD app(s) not Healthy |
| 4 | Catalog lookup(s) failed |

## Config

Files live in `configs/`, selected by `--env=<name>` → `config.<name>.json`. Copy `config.sample.json` for a new environment.

### `defaults`

| Field | Default |
|---|---|
| `timeout_seconds` | `5` |
| `retry_attempts` | `2` |
| `retry_delay_ms` | `1000` |
| `response_time_warn_ms` | `null` |

### `kibana`

| Field | Description |
|---|---|
| `url` | Kibana base URL |
| `data_view_id` | Fallback data view ID |
| `log_minutes` | Log link time window (default `5`) |
| `query_template` | KQL query; `{service}` is substituted at runtime |
| `columns` | Discover columns to pre-select (e.g. `["level", "message"]`) |

### `catalog`

| Field | Description |
|---|---|
| `base_url` | ServiceKatalog API base |
| `environment` | `dev` / `stg` / `prd` |
| `k8s_domain` | Domain suffix for URL construction |
| `argocd_server` | ArgoCD base URL |
| `argocd_token` | JWT — set to `null`, supply via env var |

Hybrid Docker + AKS deployments are handled automatically: the tool matches the most-recent version and any variant with a commit-SHA suffix (e.g. `1.70.2.37` and `1.70.2.37-4f694a1`), collecting all targets in one run.

### `services[]`

| Field | Description |
|---|---|
| `catalog_name` | Name in ServiceKatalog |
| `alias` | Short name for `--service` |
| `port` | Port for VM URL construction |
| `healthcheck_contains` | Expected response body string |
| `retry_attempts` / `timeout_seconds` / `response_time_warn_ms` | Per-service overrides |
| `kibana_data_view_id` | Service-level data view override |
| `regions` | Map of region name → region config |

### `regions`

| Field | Description |
|---|---|
| `catalog_platform` | Platform code (e.g. `eur`) |
| `catalog_region` | Region code (e.g. `euw`) |
| `kibana_data_view_id` | Region-level data view (highest priority) |
| `vm_targets` | Hardcoded VM FQDNs (skips catalog) |
| `kubernetes_url` | Hardcoded k8s ingress URL (skips catalog) |
| `vm_url_template` | Custom VM URL pattern. Placeholders: `{service}` `{target}` `{platform}` `{region}` `{env}` `{domain}` `{port}` |

## Adding a service

```jsonc
{
  "catalog_name": "web-myservice",
  "alias": "mysvc",
  "port": 8080,
  "healthcheck_contains": "Healthy",
  "regions": {
    "europe": {
      "catalog_platform": "eur",
      "catalog_region": "euw",
      "kibana_data_view_id": "<id>"
    }
  }
}
```

Test before running against prod: `dotnet run -- --env=dev --service=mysvc --mock-fail`

If checks fail with DNS errors, set `vm_url_template` on the region.

## Tests

```bash
dotnet test --filter "Category!=Integration"
RUN_INTEGRATION_TESTS=1 HEALTHCHECKER_ARGOCD_TOKEN=<jwt> dotnet test --filter "Category=Integration"
```
