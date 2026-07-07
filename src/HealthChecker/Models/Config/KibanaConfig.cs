using System.Text.Json.Serialization;

namespace HealthChecker.Models;

record KibanaConfig(
    [property: JsonPropertyName("url")]
    string Url,
    // Fallback data view ID. When null, services without a region/service-level override
    // will print a warning instead of a Kibana URL on failure.
    [property: JsonPropertyName("data_view_id")]
    string? DataViewId    = null,
    [property: JsonPropertyName("log_minutes")]
    int LogMinutes        = 5,
    // KQL query template; {service} is replaced with catalog_name at runtime.
    [property: JsonPropertyName("query_template")]
    string QueryTemplate  = "level: \"Error\"",
    // Kibana Discover columns to pre-select in the generated URL.
    // When null/empty, Kibana falls back to its default view (all fields, dense).
    // Example: ["level", "host.name", "message", "logger", "exceptionType", "exceptionMessage"]
    [property: JsonPropertyName("columns")]
    string[]? Columns     = null
);
