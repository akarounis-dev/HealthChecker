using HealthChecker.Models;

namespace HealthChecker.Application;

/// <summary>
/// Expands a resolved <see cref="RegionConfig"/> into one or more concrete check URLs.
/// Pure static logic - no runner state, no side effects.
/// </summary>
static class TargetUrlBuilder
{
    /// <summary>
    /// Yields one (Svc, Region, Url, Retries, TimeoutSec) tuple per URL to check.
    /// Throws <see cref="InvalidOperationException"/> when the region has no usable
    /// targets or is missing required catalog config.
    /// </summary>
    public static IEnumerable<(ServiceConfig Svc, string Region, string Url, int Retries, int TimeoutSec)>
        ExpandTargets(
            ServiceConfig svc, string regionName, RegionConfig rc,
            int retries, int timeoutSec,
            string? catalogEnvironment, string? kubernetesDomain)
    {
        var anyUrl = false;

        if (rc.KubernetesUrl is not null)
        {
            yield return (svc, regionName, rc.KubernetesUrl, retries, timeoutSec);
            anyUrl = true;
        }

        if (rc.VmTargets is { Length: > 0 })
        {
            if (rc.VmUrlTemplate is null && (rc.CatalogPlatform is null || rc.CatalogRegion is null))
                throw new InvalidOperationException(
                    $"catalog_platform and catalog_region are required for VM region [{regionName}] " +
                    $"in service [{svc.CatalogName}] unless vm_url_template is set.");

            // When a vm_url_template is set it may not reference {env} or {domain}, so the
            // catalog section is only required when the default URL pattern is used.
            if (rc.VmUrlTemplate is null && (catalogEnvironment is null || kubernetesDomain is null))
                throw new InvalidOperationException(
                    $"The 'catalog' section is required in the config file when VM targets are present " +
                    $"and no vm_url_template is set (service [{svc.CatalogName}], region [{regionName}]).");

            foreach (var target in rc.VmTargets)
            {
                var url = rc.VmUrlTemplate is not null
                    ? rc.VmUrlTemplate
                        .Replace("{service}",  svc.CatalogName)
                        .Replace("{target}",   target)
                        .Replace("{platform}", rc.CatalogPlatform ?? "")
                        .Replace("{region}",   rc.CatalogRegion   ?? "")
                        .Replace("{env}",      catalogEnvironment)
                        .Replace("{domain}",   kubernetesDomain)
                        .Replace("{port}",     svc.Port.ToString())
                    : $"http://{svc.CatalogName}.{target}.{rc.CatalogPlatform}-{rc.CatalogRegion}" +
                      $".{catalogEnvironment}-{kubernetesDomain}:{svc.Port}/";
                yield return (svc, regionName, url, retries, timeoutSec);
                anyUrl = true;
            }
        }

        if (!anyUrl)
            throw new InvalidOperationException(
                $"No targets resolved for region [{regionName}] in service [{svc.CatalogName}].");
    }
}
