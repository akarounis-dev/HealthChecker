using System.Collections.Concurrent;
using System.Net;

namespace HealthChecker.Tests.Helpers;

/// <summary>
/// Queued fake HTTP handler. Enqueue responses or exceptions in order;
/// each SendAsync call dequeues the next one.
/// Thread-safe: uses ConcurrentQueue so it is safe with Task.WhenAll fan-outs.
/// </summary>
sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private record Entry(HttpResponseMessage? Response, Exception? Exception, int DelayMs = 0);

    private readonly ConcurrentQueue<Entry> _queue = new();

    public void EnqueueResponse(HttpStatusCode status, string content = "")
        => _queue.Enqueue(new Entry(
            new HttpResponseMessage(status) { Content = new StringContent(content) }, null));

    public void EnqueueResponse(HttpResponseMessage response)
        => _queue.Enqueue(new Entry(response, null));

    /// <summary>Simulates a connection-level failure (no HTTP response).</summary>
    public void EnqueueException(Exception ex)
        => _queue.Enqueue(new Entry(null, ex));

    /// <summary>Simulates a slow response that will trigger the caller's CancellationToken.</summary>
    public void EnqueueDelay(int milliseconds)
        => _queue.Enqueue(new Entry(null, null, milliseconds));

    public int RemainingResponses => _queue.Count;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_queue.TryDequeue(out var entry))
            throw new InvalidOperationException(
                $"MockHttpMessageHandler has no queued response for {request.RequestUri}");

        if (entry.DelayMs > 0)
            await Task.Delay(entry.DelayMs, cancellationToken); // throws OperationCanceledException on timeout

        if (entry.Exception is not null)
            throw entry.Exception;

        return entry.Response!;
    }
}
