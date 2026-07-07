using System.Text.Json.Serialization;

namespace HealthChecker.Models;

record ServiceConfig(
    [property: JsonPropertyName("catalog_name")]
    string CatalogName,
    [property: JsonPropertyName("port")]
    int Port,
    [property: JsonPropertyName("healthcheck_contains")]
    string HealthcheckContains,
    [property: JsonPropertyName("regions")]
    Dictionary<string, RegionConfig> Regions,
    [property: JsonPropertyName("retry_attempts")]
    int? RetryAttempts       = null,
    [property: JsonPropertyName("timeout_seconds")]
    int? TimeoutSeconds      = null,
    // Short identifier matched against --service. Falls back to CatalogName when not set.
    [property: JsonPropertyName("alias")]
    string? Alias            = null,
    // Overrides kibana.data_view_id for this service's Kibana log link
    [property: JsonPropertyName("kibana_data_view_id")]
    string? KibanaDataViewId   = null,
    // Per-service response time warn threshold; overrides defaults.response_time_warn_ms
    [property: JsonPropertyName("response_time_warn_ms")]
    int?    ResponseTimeWarnMs = null
)
{
    /// <summary>The identifier matched against --service. Falls back to CatalogName.</summary>
    public string ServiceAlias => Alias ?? CatalogName;
}
