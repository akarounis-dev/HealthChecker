using System.Diagnostics;
using HealthChecker.Models;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace HealthChecker.Checks;

static class Checker
{
    public static async Task<CheckResult> CheckUrl(
        HttpClient http, string url, string expected,
        int retries, int timeoutSec, int retryDelayMs)
    {
        var sw = Stopwatch.StartNew();

        // Polly v8 requires MaxRetryAttempts >= 1; skip the retry strategy entirely when no retries are wanted.
        var builder = new ResiliencePipelineBuilder();
        if (retries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = retries,
                Delay            = TimeSpan.FromMilliseconds(retryDelayMs),
                BackoffType      = DelayBackoffType.Exponential,
                MaxDelay         = TimeSpan.FromSeconds(30),
                ShouldHandle     = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<BodyMismatchException>(),
            });
        }
        var pipeline = builder
            .AddTimeout(TimeSpan.FromSeconds(timeoutSec))
            .Build();

        try
        {
            await pipeline.ExecuteAsync(async ct =>
            {
                var response = await http.GetAsync(url, ct);
                var body     = await response.Content.ReadAsStringAsync(ct);

                if (!body.Contains(expected, StringComparison.OrdinalIgnoreCase))
                    throw new BodyMismatchException(
                        $"Unexpected response ({(int)response.StatusCode}): \"{body[..Math.Min(100, body.Length)]}\"");
            });

            sw.Stop();
            return new CheckResult(url, true, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CheckResult(url, false, sw.ElapsedMilliseconds, FormatError(ex, timeoutSec));
        }
    }

    static string FormatError(Exception ex, int timeoutSec) => ex switch
    {
        TimeoutRejectedException or OperationCanceledException              => $"Timeout after {timeoutSec}s",
        HttpRequestException { InnerException: { } inner }                  => inner.Message,
        _                                                                    => ex.Message,
    };
}

/// <summary>
/// Thrown when the HTTP response is received but doesn't contain the expected health-check string.
/// Signals Polly to retry rather than treating the attempt as a hard failure.
/// </summary>
sealed class BodyMismatchException(string message) : Exception(message);
