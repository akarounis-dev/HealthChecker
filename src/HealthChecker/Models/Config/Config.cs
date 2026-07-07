using System.Text.Json.Serialization;

namespace HealthChecker.Models;

record Config(
    [property: JsonPropertyName("defaults")]
    Defaults? Defaults,
    [property: JsonPropertyName("services")]
    ServiceConfig[] Services,
    [property: JsonPropertyName("catalog")]
    CatalogConfig? Catalog = null,
    [property: JsonPropertyName("kibana")]
    KibanaConfig? Kibana   = null
);
