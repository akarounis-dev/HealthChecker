using FluentAssertions;
using HealthChecker.Models;
using Xunit;

namespace HealthChecker.Tests.Unit;

/// <summary>
/// Tests for computed properties and lightweight logic on model records.
/// </summary>
public class ModelTests
{
    static ServiceConfig MakeSvc(
        string catalogName = "web-feed",
        string? alias      = null)
        => new(CatalogName:        catalogName,
               Port:               8080,
               HealthcheckContains:"ok",
               Regions:            [],
               Alias:              alias);

    // ── ServiceConfig.ServiceAlias ────────────────────────────────────────────

    [Fact]
    public void ServiceAlias_Returns_Alias_When_Set()
    {
        var svc = MakeSvc(catalogName: "web-sportsbookfeed", alias: "spt");
        svc.ServiceAlias.Should().Be("spt");
    }

    [Fact]
    public void ServiceAlias_Falls_Back_To_CatalogName_When_No_Alias_Set()
    {
        var svc = MakeSvc(catalogName: "web-sportsbookfeed");
        svc.ServiceAlias.Should().Be("web-sportsbookfeed");
    }

    // ── HealthCheckReport.ExitCode (bit-flag) ─────────────────────────────────

    [Fact]
    public void ExitCode_Is_Zero_When_All_Checks_Healthy()
    {
        MakeReport(httpFail: false, argoFail: false, catalogFail: false)
            .ExitCode.Should().Be(0);
    }

    [Fact]
    public void ExitCode_Is_One_When_Http_Check_Fails()
    {
        MakeReport(httpFail: true, argoFail: false, catalogFail: false)
            .ExitCode.Should().Be(1);
    }

    [Fact]
    public void ExitCode_Is_Two_When_ArgoCD_App_Is_Degraded()
    {
        MakeReport(httpFail: false, argoFail: true, catalogFail: false)
            .ExitCode.Should().Be(2);
    }

    [Fact]
    public void ExitCode_Is_Three_When_Both_Http_And_ArgoCD_Fail()
    {
        MakeReport(httpFail: true, argoFail: true, catalogFail: false)
            .ExitCode.Should().Be(3);
    }

    [Fact]
    public void ExitCode_Is_Four_When_Catalog_Resolution_Failed()
    {
        MakeReport(httpFail: false, argoFail: false, catalogFail: true)
            .ExitCode.Should().Be(4);
    }

    [Fact]
    public void ExitCode_Combines_All_Bits_When_Catalog_Http_And_ArgoCD_All_Fail()
    {
        // All three failure types OR together: 4 | 1 | 2 = 7.
        // Catalog failure no longer masks HTTP/ArgoCD results (partial catalog failures are allowed).
        MakeReport(httpFail: true, argoFail: true, catalogFail: true)
            .ExitCode.Should().Be(7);
    }

    // ── HealthCheckReport.SlowInstances ──────────────────────────────────────

    [Fact]
    public void SlowInstances_Is_Zero_When_No_Threshold_Set()
    {
        var svc   = MakeSvc();
        var entry = new CheckEntry(svc, "europe", new CheckResult("http://x/", true, 9999, null));
        var report = MakeReport(false, false, false) with
        {
            HttpResults        = [entry],
            ResponseTimeWarnMs = null      // no global threshold
        };

        report.SlowInstances.Should().Be(0);
    }

    [Fact]
    public void SlowInstances_Counts_Healthy_Checks_Exceeding_Global_Threshold()
    {
        var svc    = MakeSvc();
        var report = new HealthCheckReport(
            ConfigPath:        "cfg.json",
            FilterRegion:      null,
            CatalogEvents:     [],
            HttpResults:
            [
                new CheckEntry(svc, "europe", new CheckResult("http://fast/", true,  200, null)), // under threshold
                new CheckEntry(svc, "europe", new CheckResult("http://slow/", true,  800, null)), // over threshold
                new CheckEntry(svc, "europe", new CheckResult("http://fail/", false, 0,   "err")), // failed - not counted
            ],
            ArgocdResults:     [],
            TokenWarning:      null,
            NoTokenMessage:    null,
            Kibana:            null,
            ResponseTimeWarnMs: 500);

        report.SlowInstances.Should().Be(1);
    }

    [Fact]
    public void SlowInstances_Per_Service_Threshold_Overrides_Global()
    {
        var svcWithOverride = MakeSvc() with { ResponseTimeWarnMs = 100 }; // tighter threshold
        var svcDefault      = MakeSvc("web-other");

        var report = new HealthCheckReport(
            ConfigPath:        "cfg.json",
            FilterRegion:      null,
            CatalogEvents:     [],
            HttpResults:
            [
                new CheckEntry(svcWithOverride, "europe", new CheckResult("http://a/", true, 300, null)), // 300 > 100 → slow
                new CheckEntry(svcDefault,      "europe", new CheckResult("http://b/", true, 300, null)), // 300 < 500 → fast
            ],
            ArgocdResults:     [],
            TokenWarning:      null,
            NoTokenMessage:    null,
            Kibana:            null,
            ResponseTimeWarnMs: 500);

        report.SlowInstances.Should().Be(1, "only the service with the 100ms override exceeds its threshold");
    }

    // ── ExitCode helper ───────────────────────────────────────────────────────

    static HealthCheckReport MakeReport(bool httpFail, bool argoFail, bool catalogFail)
    {
        var svc = MakeSvc();

        IReadOnlyList<CheckEntry> httpResults =
        [
            new CheckEntry(svc, "europe",
                httpFail
                    ? new CheckResult("http://x/", false, 0,   "error")
                    : new CheckResult("http://x/", true,  100, null))
        ];

        IReadOnlyList<ArgocdEntry> argoResults = argoFail
            ? [new ArgocdEntry("europe", "web-feed", "web-feed-app", "http://argo/", "Degraded", [], 0)]
            : [];

        IReadOnlyList<CatalogEvent> catalogEvents = catalogFail
            ? [new CatalogEvent("web-feed", "europe", false, null, "timeout")]
            : [];

        return new HealthCheckReport(
            ConfigPath:    "cfg.json",
            FilterRegion:  null,
            CatalogEvents: catalogEvents,
            HttpResults:   httpResults,
            ArgocdResults: argoResults,
            TokenWarning:  null,
            NoTokenMessage:null,
            Kibana:        null);
    }
}
