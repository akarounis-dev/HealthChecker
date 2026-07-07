using Spectre.Console;

namespace HealthChecker.Output;

/// <summary>
/// Displays an animated spinner while an async task runs using Spectre.Console.
/// Automatically suppressed when stdout is redirected (e.g. piped or --json mode).
/// </summary>
static class Spinner
{
    public static async Task<T> RunAsync<T>(Task<T> work, string label = "Running health checks...")
    {
        if (!Environment.UserInteractive || Console.IsOutputRedirected)
            return await work;

        return await AnsiConsole.Status()
            .Spinner(Spectre.Console.Spinner.Known.Dots)
            .StartAsync(label, _ => work);
    }
}
