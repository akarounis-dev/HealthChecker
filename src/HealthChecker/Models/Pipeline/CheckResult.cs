namespace HealthChecker.Models;

record CheckResult(string Url, bool Healthy, long ElapsedMs, string? Error);
