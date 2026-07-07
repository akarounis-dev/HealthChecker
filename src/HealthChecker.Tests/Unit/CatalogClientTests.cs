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
        // The two versions in this fixture are "1.0.0" and "2.0.0" — unrelated base
        // versions. Only the newer one (2.0.0) matches; 1.0.0 is not a commit-SHA
        // variant of 2.0.0, so it is excluded.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.TwoVersionsResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.ResolvedVersion.Should().Be(CatalogJson.LatestVersion);
        result.ResolvedVersion.Should().NotBe(CatalogJson.OlderVersion);
    }

    // ── StripCommitSuffix ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.70.2.37-4f694a1",  "1.70.2.37")]   // 7-char lowercase hex SHA
    [InlineData("1.70.2.37-ab80594",  "1.70.2.37")]   // another 7-char lowercase hex SHA
    [InlineData("1.70.2.37-4F694A1",  "1.70.2.37")]   // 7-char uppercase hex SHA
    [InlineData("1.70.2.37-AB80594",  "1.70.2.37")]   // another 7-char uppercase hex SHA
    [InlineData("1.70.2.37",          "1.70.2.37")]   // no suffix
    [InlineData("1.51.4.1",           "1.51.4.1")]    // numeric minor — not a SHA
    [InlineData("1.70.2.37-dotcover", "1.70.2.37-dotcover")] // non-hex suffix
    public void StripCommitSuffix_Returns_Base_Version(string input, string expected)
    {
        CatalogClient.StripCommitSuffix(input).Should().Be(expected);
    }

    [Fact]
    public async Task Merges_Targets_Across_Multiple_Versions_For_Same_Region()
    {
        // Real-world hybrid: Docker VMs under version "1.70.2.37" and a k8s cluster
        // under "1.70.2.37-4f694a1" are both deployed simultaneously.
        // The client must collect targets from all versions, not just the most recent.
        var handler = new MockHttpMessageHandler();
        handler.EnqueueResponse(HttpStatusCode.OK, CatalogJson.CrossVersionHybridResponse());

        var result = await CatalogClient.FetchInstances(
            Client(handler), BaseUrl, SvcName, null, Env, Platform, Region);

        result.VmTargets.Should().BeEquivalentTo(CatalogJson.CrossVersionVmTargets);
        result.KubernetesCluster.Should().Be(CatalogJson.K8sCluster);
        result.ResolvedVersion.Should().Contain(CatalogJson.CrossVersionDocker);
        result.ResolvedVersion.Should().Contain(CatalogJson.CrossVersionAks);
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
