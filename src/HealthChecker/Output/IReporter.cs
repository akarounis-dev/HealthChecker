using HealthChecker.Models;

namespace HealthChecker.Output;

/// <summary>
/// Abstraction over any output channel (console, JSON, HTTP API, …).
/// Implementations receive the completed <see cref="HealthCheckReport"/> and
/// render it however they see fit - the runner has no knowledge of them.
/// </summary>
interface IReporter
{
    void Report(HealthCheckReport report);
}
