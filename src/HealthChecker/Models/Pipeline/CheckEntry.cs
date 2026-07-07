namespace HealthChecker.Models;

record CheckEntry(ServiceConfig Svc, string Region, CheckResult Result);
