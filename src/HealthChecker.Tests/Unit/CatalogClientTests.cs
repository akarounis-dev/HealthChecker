using System.Net;
using FluentAssertions;
using HealthChecker.Clients;
using HealthChecker.Tests.Helpers;
using Xunit;

namespace HealthChecker.Tests.Unit;

public class CatalogClientTests
{
    const string BaseUrl  = "http://catalog/api/v1/services";
    const string SvcName  = "web-testservice";
    const string Env      = "prd";
    const string Platform = "eur";
    const string Region   = "euw";

    static HttpClient Client(MockHttpMessageHandler handler) => new(handler);

    // ── Target classification ─────────────────────────────────────────────────

    [Fact]
    public async Task Parses_VM_Targets_With_Numeric_Suffix()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.VmTargetsResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.VmTargets.Should().BeEquivalentTo(CatalogJson.VmTargets);
        result.KubernetesCluster.Should().BeNull();
    }

    [Fact]
    public async Task Parses_K8s_Cluster_With_Non_Numeric_Suffix()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.K8sClusterResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.KubernetesCluster.Should().Be(CatalogJson.K8sCluster);
        result.VmTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task Parses_Hybrid_Deployment_With_Both_VM_And_K8s_Targets()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.HybridResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.VmTargets.Should().HaveCount(2);
        result.KubernetesCluster.Should().Be(CatalogJson.K8sCluster);
    }

    // ── Version resolution ────────────────────────────────────────────────────

    [Fact]
    public async Task Resolves_Latest_Version_By_Event_Time()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.TwoVersionsResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.ResolvedVersion.Should().Be(CatalogJson.LatestVersion);
        result.ResolvedVersion.Should().NotBe(CatalogJson.OlderVersion);
    }

    [Fact]
    public async Task Uses_Pinned_Version_In_URL_When_Supplied()
    {
        var handler = new MockHttpMessageHandler();
        // Pin to the version that's in the VM-targets fixture
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.VmTargetsResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, CatalogJson.VmVersion, Env, Platform, Region);

        result.ResolvedVersion.Should().Be(CatalogJson.VmVersion);
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Throws_When_No_Matching_Environment_Found()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.NoMatchingEnvResponse());

        var act = () => CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No deployment found*");
    }

    [Fact]
    public async Task Throws_When_Catalog_Returns_Empty_Versions()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.EmptyResponse());

        var act = () => CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Throws_When_Catalog_Request_Fails()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "oops");

        var act = () => CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Catalog request failed*");
    }

    [Fact]
    public async Task VM_Targets_Are_Returned_Sorted()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.UnsortedTargetsResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.VmTargets.Should().BeInAscendingOrder();
    }
}
