using HealthChecker.Models;

namespace HealthChecker.Application;

static class ConfigValidator
{
    /// <summary>
    /// Validates the deserialized config and returns a list of human-readable error strings.
    /// An empty list means the config is valid and the runner may proceed.
    /// </summary>
    public static List<string> Validate(Config config)
    {
        var errors = new List<string>();

        // ── catalog section ───────────────────────────────────────────────────

        bool hasCatalog = config.Catalog is not null;
        if (hasCatalog)
        {
            if (string.IsNullOrWhiteSpace(config.Catalog!.BaseUrl))
                errors.Add("catalog.base_url is required.");
            if (string.IsNullOrWhiteSpace(config.Catalog.Environment))
                errors.Add("catalog.environment is required.");
            if (string.IsNullOrWhiteSpace(config.Catalog.KubernetesDomain))
                errors.Add("catalog.k8s_domain is required.");
        }

        // ── services ──────────────────────────────────────────────────────────

        if (config.Services is null || config.Services.Length == 0)
        {
            errors.Add("services[] must contain at least one entry.");
            return errors;
        }

        // Duplicate alias check - each service must resolve to a unique alias.
        var aliasCounts = config.Services
            .Select(s => s.ServiceAlias.ToLowerInvariant())
            .GroupBy(a => a)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var dup in aliasCounts)
            errors.Add($"Duplicate alias '{dup}' - each service must have a unique alias " +
                       $"(or catalog_name when alias is not set).");

        for (int i = 0; i < config.Services.Length; i++)
        {
            var svc   = config.Services[i];
            var label = $"services[{i}] ({svc.CatalogName ?? "(unnamed)"})";

            if (string.IsNullOrWhiteSpace(svc.CatalogName))
                errors.Add($"{label}: catalog_name is required.");
            if (svc.Port <= 0)
                errors.Add($"{label}: port must be a positive integer (got {svc.Port}).");
            if (string.IsNullOrWhiteSpace(svc.HealthcheckContains))
                errors.Add($"{label}: healthcheck_contains is required.");
            if (svc.Regions is null || svc.Regions.Count == 0)
                errors.Add($"{label}: must define at least one region.");

            if (svc.Regions is not null)
            {
                foreach (var (regionName, rc) in svc.Regions)
                {
                    var rlabel = $"{label}, region '{regionName}'";

                    bool hasPlatform       = !string.IsNullOrWhiteSpace(rc.CatalogPlatform);
                    bool hasRegionCode     = !string.IsNullOrWhiteSpace(rc.CatalogRegion);
                    bool hasVmTargets      = rc.VmTargets is { Length: > 0 };
                    bool hasK8sUrl         = !string.IsNullOrWhiteSpace(rc.KubernetesUrl);
                    bool canUseCatalog     = hasPlatform && hasRegionCode;
                    bool hasHardcoded      = hasVmTargets || hasK8sUrl;

                    if (!canUseCatalog && !hasHardcoded)
                        errors.Add($"{rlabel}: must have catalog_platform+catalog_region, " +
                                   $"vm_targets, or kubernetes_url.");

                    if (hasPlatform != hasRegionCode)
                        errors.Add($"{rlabel}: catalog_platform and catalog_region must both " +
                                   $"be set or both omitted.");

                    // vm_targets require a way to construct URLs: either platform+region (for the default
                    // template) or an explicit vm_url_template. Without one of these, the runner will fail.
                    if (hasVmTargets && !canUseCatalog && string.IsNullOrWhiteSpace(rc.VmUrlTemplate))
                        errors.Add($"{rlabel}: vm_targets requires catalog_platform+catalog_region " +
                                   $"or vm_url_template to construct target URLs.");

                    if (canUseCatalog && !hasCatalog)
                        errors.Add($"{rlabel}: uses catalog resolution but the 'catalog' " +
                                   $"section is missing from the config.");
                }
            }
        }

        return errors;
    }
}
