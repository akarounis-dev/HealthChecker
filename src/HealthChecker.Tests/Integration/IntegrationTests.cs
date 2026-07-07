using FluentAssertions;
using HealthChecker.Checks;
using HealthChecker.Clients;
using Xunit;

namespace HealthChecker.Tests.Integration;

/// <summary>
/// Smoke tests that hit real dev infrastructure.
///
/// These are intentionally NOT exhaustive - they verify connectivity and basic
/// response shape. Exhaustive path coverage lives in the unit tests.
///
/// To run: set the environment variable RUN_INTEGRATION_TESTS=1
///   dotnet test --filter "Category=Integration"
///
/// Credentials are read from environment variables so they never appear in source:
///   HEALTHCHECKER_ARGOCD_TOKEN   - ArgoCD JWT (from configs/config.dev.json)
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests
{
    // xUnit v2 has no built-in dynamic skip. Tests return early and show as Passed when
    // the env var is not set. Use --filter "Category!=Integration" in CI to exclude them.
    static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") != "1";

    const string DevServiceUrl  = "http://web-sportsbookfeed.eur-sec.dev-novibet.systems/";
    const string DevCatalogBase = "https://servicekatalog.dvo-novibet.systems/api/v1/services";
    const string DevArgoServer  = "https://argo.k8s.dvo-novibet.systems";
    const string DevArgoApp     = "web-sportsbookfeed-eur-sec-dev";

    // ── HTTP health check ─────────────────────────────────────────────────────

    [Fact]
    public async Task Dev_SportsbookFeed_HealthEndpoint_Responds_Healthy()
    {
        if (ShouldSkip) return;

        using var http = new HttpClient();
        var result = await Checker.CheckUrl(
            http, DevServiceUrl,
            expected:     "SportsbookFeed is live!",
            retries:      1,
            timeoutSec:   10,
            retryDelayMs: 500);

        result.Healthy.Should().BeTrue(
            $"expected dev service at {DevServiceUrl} to be healthy");
        result.ElapsedMs.Should().BeGreaterThan(0);
    }

    // ── Catalog API ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Dev_Catalog_Returns_Valid_Response_For_SportsbookFeed()
    {
        if (ShouldSkip) return;

        using var http = new HttpClient();
        var result = await CatalogClient.FetchInstances(
            http, DevCatalogBase,
            serviceName:     "web-sportsbookfeed",
            version:         null,
            catalogEnv:      "dev",
            catalogPlatform: "eur",
            catalogRegion:   "sec");

        result.ResolvedVersion.Should().NotBeNullOrEmpty();
        (result.VmTargets.Length + (result.KubernetesCluster is not null ? 1 : 0))
            .Should().BeGreaterThan(0, "at least one target must be present");
    }

    // ── ArgoCD API ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dev_ArgoCD_Returns_App_Health_For_SportsbookFeed()
    {
        if (ShouldSkip) return;

        var token = Environment.GetEnvironmentVariable("HEALTHCHECKER_ARGOCD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            // Provide a clear message rather than silently passing
            throw new InvalidOperationException(
                "Set HEALTHCHECKER_ARGOCD_TOKEN env var to run ArgoCD integration tests.");
        }

        var (status, _, podCount) = await ArgocdClient.GetAppHealth(
            DevArgoServer, token, DevArgoApp, timeoutSec: 15);

        status.Should().NotBe("Unknown",
            "ArgoCD should return a real status, not Unknown (check token validity)");
        podCount.Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task Dev_ArgoCD_Returns_Ingress_Url_For_SportsbookFeed()
    {
        if (ShouldSkip) return;

        var token = Environment.GetEnvironmentVariable("HEALTHCHECKER_ARGOCD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Set HEALTHCHECKER_ARGOCD_TOKEN env var to run ArgoCD integration tests.");

        var (url, reason) = await ArgocdClient.GetAppIngress(
            DevArgoServer, token, DevArgoApp, timeoutSec: 15);

        // The dev cluster has an ingress - we expect a URL back
        url.Should().NotBeNull(
            $"expected an ingress URL from ArgoCD for {DevArgoApp}; reason: {reason}");
        url.Should().MatchRegex("^https?://");
    }

    // ── Token validity ────────────────────────────────────────────────────────

    [Fact]
    public void Dev_ArgoCD_Token_Is_Not_Expired()
    {
        if (ShouldSkip) return;

        var token = Environment.GetEnvironmentVariable("HEALTHCHECKER_ARGOCD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Set HEALTHCHECKER_ARGOCD_TOKEN env var to run ArgoCD integration tests.");

        var timeLeft = ArgocdClient.GetTokenTimeLeft(token);

        timeLeft.Should().NotBeNull("token should be a valid JWT");
        timeLeft!.Value.Should().BePositive("token should not be expired");
    }
}
