using System.Text.Json.Serialization;

namespace HealthChecker.Models;

record Defaults(
    [property: JsonPropertyName("timeout_seconds")]
    int  TimeoutSeconds      = 5,
    [property: JsonPropertyName("retry_attempts")]
    int  RetryAttempts       = 2,
    [property: JsonPropertyName("retry_delay_ms")]
    int  RetryDelayMs        = 1000,
    // When set, healthy checks that exceed this threshold render with a WN (warn) indicator.
    [property: JsonPropertyName("response_time_warn_ms")]
    int? ResponseTimeWarnMs  = null
);
