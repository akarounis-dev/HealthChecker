using System.Net;
using System.Text;
using FluentAssertions;
using HealthChecker.Clients;
using HealthChecker.Tests.Helpers;
using Xunit;

namespace HealthChecker.Tests.Unit;

public class ArgocdClientTests
{
    const string Server  = "http://argo.example.com";
    const string Token   = "fake-token";
    const string AppName = "my-app";

    // ── GetTokenTimeLeft ──────────────────────────────────────────────────────

    [Fact]
    public void GetTokenTimeLeft_Returns_Positive_TimeSpan_For_Valid_Future_Token()
    {
        var token = MakeJwt(DateTimeOffset.UtcNow.AddHours(24));
        var result = ArgocdClient.GetTokenTimeLeft(token);
        result.Should().NotBeNull();
        result!.Value.Should().BePositive();
    }

    [Fact]
    public void GetTokenTimeLeft_Returns_Negative_TimeSpan_For_Expired_Token()
    {
        var token = MakeJwt(DateTimeOffset.UtcNow.AddHours(-1));
        var result = ArgocdClient.GetTokenTimeLeft(token);
        result.Should().NotBeNull();
        result!.Value.Should().BeNegative();
    }

    [Fact]
    public void GetTokenTimeLeft_Returns_Null_For_Malformed_Token()
    {
        ArgocdClient.GetTokenTimeLeft("not.a.jwt").Should().BeNull();
        ArgocdClient.GetTokenTimeLeft("plain-string").Should().BeNull();
        ArgocdClient.GetTokenTimeLeft("").Should().BeNull();
    }

    [Fact]
    public void GetTokenTimeLeft_Returns_Null_When_Payload_Has_No_Exp_Claim()
    {
        var payload = Base64UrlEncode("""{"sub":"user","iat":1000}""");
        var token   = $"header.{payload}.signature";
        ArgocdClient.GetTokenTimeLeft(token).Should().BeNull();
    }

    // ── GetAppHealth ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAppHealth_Returns_Healthy_Status()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.AppHealthy());
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTree(ArgocdJson.ResourceTreePods3));

        var (status, degraded, podCount) = await ArgocdClient.GetAppHealth(
            Server, Token, AppName, handler: handler);

        status.Should().Be("Healthy");
        degraded.Should().BeEmpty();
        podCount.Should().Be(ArgocdJson.ResourceTreePods3);
    }

    [Fact]
    public async Task GetAppHealth_Returns_Degraded_Resources()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.AppDegraded());
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTree(ArgocdJson.ResourceTreePods3));

        var (status, degraded, _) = await ArgocdClient.GetAppHealth(
            Server, Token, AppName, handler: handler);

        status.Should().Be("Degraded");
        // fixture has Deployment(Degraded) + ReplicaSet(Progressing); Service(Missing) is filtered out
        degraded.Should().HaveCount(2);
        degraded.Should().Contain(d => d.Contains(ArgocdJson.DegradedDeployment));
        degraded.Should().Contain(d => d.Contains(ArgocdJson.DegradedReplicaSet));
    }

    [Fact]
    public async Task GetAppHealth_Skips_Healthy_And_Missing_Resources_In_Degraded_List()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.AppHealthFilterTest());
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTree());

        var (_, degraded, _) = await ArgocdClient.GetAppHealth(
            Server, Token, AppName, handler: handler);

        // fixture has Deployment(Healthy) + Service(Missing) + ReplicaSet(Progressing)
        // only the Progressing ReplicaSet should appear
        degraded.Should().HaveCount(1);
        degraded[0].Should().Contain("ReplicaSet");
    }

    [Fact]
    public async Task GetAppHealth_Returns_Unknown_On_Http_Error_With_Message()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.Unauthorized, ArgocdJson.ErrorUnauthorized());

        var (status, degraded, _) = await ArgocdClient.GetAppHealth(
            Server, Token, AppName, handler: handler);

        status.Should().Be("Unknown");
        degraded.Should().ContainSingle().Which.Should().Contain(ArgocdJson.UnauthorizedMessage);
    }

    [Fact]
    public async Task GetAppHealth_Returns_Unknown_On_Request_Exception()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("network error"));

        var (status, degraded, podCount) = await ArgocdClient.GetAppHealth(
            Server, Token, AppName, handler: handler);

        status.Should().Be("Unknown");
        degraded.Should().ContainSingle().Which.Should().Contain("network error");
        podCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAppHealth_Reads_Pod_Count_From_ResourceTree_Endpoint()
    {
        // ArgoCD app health status.resources lists Deployments/Services/etc - not pods.
        // Pod count is therefore always read from the separate resource-tree endpoint.
        // Verify a different fixture pod count (5) is correctly returned.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.AppHealthy());
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTree(ArgocdJson.ResourceTreePods5));

        var (_, _, podCount) = await ArgocdClient.GetAppHealth(
            Server, Token, AppName, handler: handler);

        podCount.Should().Be(ArgocdJson.ResourceTreePods5);
        handler.RemainingResponses.Should().Be(0, "resource-tree must always be fetched for pod count");
    }

    // ── GetAppIngress ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAppIngress_Returns_Url_From_Standard_Ingress()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTreeWithIngress());
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.IngressManifest());

        var (url, reason) = await ArgocdClient.GetAppIngress(
            Server, Token, AppName, handler: handler);

        url.Should().Be($"http://{ArgocdJson.IngressHost}/");
        reason.Should().BeNull();
    }

    [Fact]
    public async Task GetAppIngress_Returns_Url_From_Traefik_IngressRoute()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTreeWithIngressRoute());
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.IngressRouteManifest());

        var (url, reason) = await ArgocdClient.GetAppIngress(
            Server, Token, AppName, handler: handler);

        url.Should().Be($"http://{ArgocdJson.IngressRouteHost}/");
        reason.Should().BeNull();
    }

    [Fact]
    public async Task GetAppIngress_Returns_Null_When_No_Ingress_In_Tree()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, ArgocdJson.ResourceTreeKinds());

        var (url, reason) = await ArgocdClient.GetAppIngress(
            Server, Token, AppName, handler: handler);

        url.Should().BeNull();
        reason.Should().Contain("No Ingress/IngressRoute");
        reason.Should().Contain("Deployment");
    }

    [Fact]
    public async Task GetAppIngress_Returns_Null_On_Request_Exception()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));

        var (url, reason) = await ArgocdClient.GetAppIngress(
            Server, Token, AppName, handler: handler);

        url.Should().BeNull();
        reason.Should().Contain("Request failed");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string MakeJwt(DateTimeOffset expiry)
    {
        var payload = Base64UrlEncode($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}");
        return $"eyJhbGciOiJSUzI1NiJ9.{payload}.signature";
    }

    static string Base64UrlEncode(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                  .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
