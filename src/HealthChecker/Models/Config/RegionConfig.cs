using System.Text.Json.Serialization;

namespace HealthChecker.Models;

record RegionConfig(
    [property: JsonPropertyName("catalog_platform")]
    string? CatalogPlatform   = null,
    [property: JsonPropertyName("catalog_region")]
    string? CatalogRegion     = null,
    [property: JsonPropertyName("timeout_seconds")]
    int? TimeoutSeconds       = null,
    [property: JsonPropertyName("retry_attempts")]
    int? RetryAttempts        = null,
    // Resolved at runtime from catalog - not set in config
    [property: JsonPropertyName("kubernetes_cluster")]
    string? KubernetesCluster = null,
    [property: JsonPropertyName("kubernetes_url")]
    string? KubernetesUrl     = null,
    [property: JsonPropertyName("vm_targets")]
    string[]? VmTargets       = null,
    // Overrides service-level and global Kibana data view ID for this region
    [property: JsonPropertyName("kibana_data_view_id")]
    string? KibanaDataViewId  = null,
    // Optional URL template for VM targets. Placeholders: {service} {target} {platform} {region} {env} {domain} {port}
    // Default (when null): http://{service}.{target}.{platform}-{region}.{env}-{domain}:{port}/
    [property: JsonPropertyName("vm_url_template")]
    string? VmUrlTemplate     = null
);
