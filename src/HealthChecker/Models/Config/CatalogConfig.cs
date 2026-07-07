using System.Text.Json.Serialization;

namespace HealthChecker.Models;

record CatalogConfig(
    [property: JsonPropertyName("base_url")]
    string BaseUrl,
    [property: JsonPropertyName("environment")]
    string Environment       = "prd",
    [property: JsonPropertyName("k8s_domain")]
    string KubernetesDomain  = "novibet.systems",
    [property: JsonPropertyName("argocd_server")]
    string? ArgocdServer     = null,
    [property: JsonPropertyName("argocd_token")]
    string? ArgocdToken      = null
);
