using System.Net;
using FluentAssertions;
using HealthChecker.Checks;
using HealthChecker.Tests.Helpers;
using Xunit;

namespace HealthChecker.Tests.Unit;

public class CheckerTests
{
    const string Url      = "http://fake-service/health";
    const string Expected = "Service is live!";

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Healthy_When_Response_Contains_Expected_String()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, $"  {Expected}  ");

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeTrue();
        result.Error.Should().BeNull();
        result.ElapsedMs.Should().BeGreaterOrEqualTo(0);
        result.Url.Should().Be(Url);
    }

    [Fact]
    public async Task Healthy_Check_Is_Case_Insensitive()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, Expected.ToUpper());

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeTrue();
    }

    // ── Failure: wrong content ────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Unhealthy_When_Response_Does_Not_Contain_Expected_String()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, "Something completely different");

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeFalse();
        result.Error.Should().Contain("Unexpected response");
        result.Error.Should().Contain("200");
    }

    [Fact]
    public async Task Error_Message_Truncates_Body_Snippet_To_100_Chars()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, new string('x', 200));

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 5, retryDelayMs: 0);

        result.Error.Should().NotBeNull();
        // body snippet in error is capped at 100 chars
        // "Unexpected response (200): \"" = 28 chars prefix, body capped at 100, + closing quote = 129 max
        result.Error!.Length.Should().BeLessOrEqualTo(130);
    }

    // ── Failure: body without expected string (various status codes) ──────────
    // Note: Checker is purely content-based and does NOT inspect HTTP status codes.
    // These tests pass because the body "error" does not contain the expected string.
    // A 500 response whose body happened to contain the expected string would be Healthy.

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Returns_Unhealthy_When_Body_Does_Not_Contain_Expected_String(HttpStatusCode status)
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(status, "error");

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeFalse();
        result.Error.Should().Contain(((int)status).ToString());
    }

    // ── Failure: network errors ───────────────────────────────────────────────

    [Fact]
    public async Task Returns_Unhealthy_On_Connection_Refused()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("Connection refused",
            new System.Net.Sockets.SocketException()));

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeFalse();
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Returns_Unhealthy_On_Timeout()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueDelay(10_000); // 10s - longer than the 1s timeout below

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 0, timeoutSec: 1, retryDelayMs: 0);

        result.Healthy.Should().BeFalse();
        result.Error.Should().Contain("Timeout");
    }

    // ── Retry logic ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_Healthy_When_Retry_Succeeds_After_Initial_Failure()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "down");
        handler.EnqueueResponse(HttpStatusCode.OK, Expected);

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 1, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeTrue();
        handler.RemainingResponses.Should().Be(0, "both responses should have been consumed");
    }

    [Fact]
    public async Task Returns_Unhealthy_When_All_Retries_Exhausted()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "down");
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "still down");
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "still down");

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 2, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeFalse();
        handler.RemainingResponses.Should().Be(0);
    }

    [Fact]
    public async Task Returns_Last_Error_When_Retries_Exhausted()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, "wrong first");
        handler.EnqueueResponse(HttpStatusCode.OK, "wrong second");

        using var http = new HttpClient(handler);
        var result = await Checker.CheckUrl(http, Url, Expected, retries: 1, timeoutSec: 5, retryDelayMs: 0);

        result.Healthy.Should().BeFalse();
        result.Error.Should().Contain("wrong second");
        handler.RemainingResponses.Should().Be(0, "both responses should have been consumed");
    }
}
