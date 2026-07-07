using System.Text.Json;
using FluentAssertions;
using HealthChecker.Models;
using HealthChecker.Output;
using Xunit;

namespace HealthChecker.Tests.Unit;

/// <summary>
/// Tests for JsonReporter - verifies the machine-readable JSON output emitted
/// by the --json flag is well-formed and contains all required fields.
/// </summary>
public class JsonReporterTests : IDisposable
{
    private readonly StringWriter _out = new();

    public void Dispose() => _out.Dispose();

    string Output => _out.ToString();

    JsonReporter Reporter => new(_out);

    static HealthCheckReport EmptyReport() => new(
        ConfigPath:    "configs/config.test.json",
        FilterRegion:  null,
        CatalogEvents: [],
        HttpResults:   [],
        ArgocdResults: [],
        TokenWarning:  null,
        NoTokenMessage:null,
        Kibana:        null);

    static ServiceConfig MakeSvc(string name = "web-feed") =>
        new(CatalogName:        name,
            Port:               8080,
            HealthcheckContains:"ok",
            Regions:            []);

    // ── Output is valid JSON ──────────────────────────────────────────────────

    [Fact]
    public void Report_Writes_Valid_JSON()
    {
        Reporter.Report(EmptyReport());

        var act = () => JsonDocument.Parse(Output);
        act.Should().NotThrow("JsonReporter must produce parseable JSON");
    }

    [Fact]
    public void Report_Uses_Snake_Case_Keys_For_Top_Level_Fields()
    {
        Reporter.Report(EmptyReport());

        using var doc  = JsonDocument.Parse(Output);
        var root = doc.RootElement;

        root.TryGetProperty("exit_code",    out _).Should().BeTrue();
        root.TryGetProperty("http_results", out _).Should().BeTrue();
        root.TryGetProperty("timestamp",    out _).Should().BeTrue();
    }

    // ── Required top-level fields ─────────────────────────────────────────────

    [Fact]
    public void Report_Includes_Timestamp_Approximately_Now()
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        Reporter.Report(EmptyReport());
        var after  = DateTime.UtcNow.AddSeconds(2);

        using var doc = JsonDocument.Parse(Output);
        var raw    = doc.RootElement.GetProperty("timestamp").GetString()!;
        var parsed = DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);

        parsed.Should().BeOnOrAfter(before);
        parsed.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Report_Includes_Config_Path()
    {
        Reporter.Report(EmptyReport());

        using var doc = JsonDocument.Parse(Output);
        doc.RootElement.GetProperty("config").GetString()
            .Should().Be("configs/config.test.json");
    }

    [Fact]
    public void Report_Includes_Exit_Code_Zero_When_All_Healthy()
    {
        var svc   = MakeSvc();
        var entry = new CheckEntry(svc, "europe", new CheckResult("http://ok/", true, 100, null));
        Reporter.Report(EmptyReport() with { HttpResults = [entry] });

        using var doc = JsonDocument.Parse(Output);
        doc.RootElement.GetProperty("exit_code").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Report_Includes_Exit_Code_One_When_Http_Fails()
    {
        var svc   = MakeSvc();
        var entry = new CheckEntry(svc, "europe", new CheckResult("http://bad/", false, 0, "err"));
        Reporter.Report(EmptyReport() with { HttpResults = [entry] });

        using var doc = JsonDocument.Parse(Output);
        doc.RootElement.GetProperty("exit_code").GetInt32().Should().Be(1);
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    [Fact]
    public void Report_Summary_Reflects_Healthy_And_Failed_Counts()
    {
        var svc = MakeSvc();
        var results = new List<CheckEntry>
        {
            new(svc, "europe", new CheckResult("http://ok/",   true,  100, null)),
            new(svc, "europe", new CheckResult("http://fail/", false, 0,   "error")),
        };

        Reporter.Report(EmptyReport() with { HttpResults = results });

        using var doc     = JsonDocument.Parse(Output);
        var summary = doc.RootElement.GetProperty("summary");
        summary.GetProperty("total").GetInt32().Should().Be(2);
        summary.GetProperty("healthy").GetInt32().Should().Be(1);
        summary.GetProperty("failed").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Report_Summary_Zeros_When_No_Http_Results()
    {
        Reporter.Report(EmptyReport());

        using var doc     = JsonDocument.Parse(Output);
        var summary = doc.RootElement.GetProperty("summary");
        summary.GetProperty("total").GetInt32().Should().Be(0);
        summary.GetProperty("healthy").GetInt32().Should().Be(0);
        summary.GetProperty("failed").GetInt32().Should().Be(0);
    }

    // ── HTTP results ──────────────────────────────────────────────────────────

    [Fact]
    public void Report_Http_Results_Are_Grouped_By_Region_And_Service()
    {
        var svc    = MakeSvc();
        var report = EmptyReport() with
        {
            HttpResults =
            [
                new CheckEntry(svc, "europe", new CheckResult("http://a/", true, 100, null)),
                new CheckEntry(svc, "europe", new CheckResult("http://b/", true, 200, null)),
            ]
        };

        Reporter.Report(report);

        using var doc    = JsonDocument.Parse(Output);
        var groups = doc.RootElement.GetProperty("http_results").EnumerateArray().ToList();
        groups.Should().HaveCount(1, "both entries share the same region+service");
        groups[0].GetProperty("checks").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Report_Http_Result_Entries_Include_Url_Healthy_And_ElapsedMs()
    {
        var svc   = MakeSvc();
        var entry = new CheckEntry(svc, "europe", new CheckResult("http://ok/", true, 150, null));
        Reporter.Report(EmptyReport() with { HttpResults = [entry] });

        using var doc   = JsonDocument.Parse(Output);
        var check = doc.RootElement
            .GetProperty("http_results").EnumerateArray().First()
            .GetProperty("checks").EnumerateArray().First();

        check.GetProperty("url").GetString().Should().Be("http://ok/");
        check.GetProperty("healthy").GetBoolean().Should().BeTrue();
        check.GetProperty("elapsed_ms").GetInt64().Should().Be(150);
    }

    [Fact]
    public void Report_Http_Result_Includes_Error_Message_When_Unhealthy()
    {
        var svc   = MakeSvc();
        var entry = new CheckEntry(svc, "europe",
            new CheckResult("http://bad/", false, 0, "connection refused"));
        Reporter.Report(EmptyReport() with { HttpResults = [entry] });

        using var doc   = JsonDocument.Parse(Output);
        var check = doc.RootElement
            .GetProperty("http_results").EnumerateArray().First()
            .GetProperty("checks").EnumerateArray().First();

        check.GetProperty("error").GetString().Should().Be("connection refused");
    }

    [Fact]
    public void Report_Http_Results_Are_Ordered_By_Region_Then_Service()
    {
        var svcA = MakeSvc("web-a");
        var svcB = MakeSvc("web-b");
        var report = EmptyReport() with
        {
            HttpResults =
            [
                new CheckEntry(svcB, "europe", new CheckResult("http://b/", true, 1, null)),
                new CheckEntry(svcA, "europe", new CheckResult("http://a/", true, 1, null)),
            ]
        };

        Reporter.Report(report);

        using var doc    = JsonDocument.Parse(Output);
        var groups = doc.RootElement.GetProperty("http_results").EnumerateArray().ToList();
        groups[0].GetProperty("service").GetString().Should().Be("web-a");
        groups[1].GetProperty("service").GetString().Should().Be("web-b");
    }

    // ── ArgoCD results ────────────────────────────────────────────────────────

    [Fact]
    public void Report_ArgoCD_Results_Include_Status_And_Pod_Count()
    {
        var argo   = new ArgocdEntry("europe", "web-feed", "web-feed-app", "http://argo/", "Healthy", [], 3);
        Reporter.Report(EmptyReport() with { ArgocdResults = [argo] });

        using var doc       = JsonDocument.Parse(Output);
        var argoArr   = doc.RootElement.GetProperty("argocd_results").EnumerateArray().ToList();
        argoArr.Should().HaveCount(1);
        argoArr[0].GetProperty("status").GetString().Should().Be("Healthy");
        argoArr[0].GetProperty("pod_count").GetInt32().Should().Be(3);
        argoArr[0].GetProperty("app_name").GetString().Should().Be("web-feed-app");
    }

    [Fact]
    public void Report_ArgoCD_Degraded_Resources_Omitted_When_Empty()
    {
        // degraded_resources is null when empty, so WhenWritingNull omits the key
        var argo = new ArgocdEntry("europe", "web-feed", "app", "http://argo/", "Healthy", [], 2);
        Reporter.Report(EmptyReport() with { ArgocdResults = [argo] });

        using var doc = JsonDocument.Parse(Output);
        var entry = doc.RootElement.GetProperty("argocd_results").EnumerateArray().First();

        // Should be absent (null → omitted) when no degraded resources
        entry.TryGetProperty("degraded_resources", out var prop);
        (prop.ValueKind == JsonValueKind.Undefined || prop.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("empty degraded_resources should be omitted");
    }

    [Fact]
    public void Report_ArgoCD_Degraded_Resources_Present_When_Non_Empty()
    {
        var argo = new ArgocdEntry("europe", "web-feed", "app", "http://argo/",
            "Degraded", ["Deployment/web-feed [Degraded]"], 2);
        Reporter.Report(EmptyReport() with { ArgocdResults = [argo] });

        using var doc = JsonDocument.Parse(Output);
        var resources = doc.RootElement
            .GetProperty("argocd_results").EnumerateArray().First()
            .GetProperty("degraded_resources").EnumerateArray().ToList();

        resources.Should().HaveCount(1);
        resources[0].GetString().Should().Contain("Degraded");
    }

    // ── Optional / nullable fields ────────────────────────────────────────────

    [Fact]
    public void Report_Includes_Token_Warning_When_Set()
    {
        Reporter.Report(EmptyReport() with { TokenWarning = "Token expires in 2h" });

        using var doc = JsonDocument.Parse(Output);
        doc.RootElement.GetProperty("token_warning").GetString()
            .Should().Be("Token expires in 2h");
    }

    [Fact]
    public void Report_Includes_Filter_Service_When_Set()
    {
        Reporter.Report(EmptyReport() with { FilterService = "feed" });

        using var doc = JsonDocument.Parse(Output);
        doc.RootElement.GetProperty("filter_service").GetString().Should().Be("feed");
    }

    // ── Catalog events ────────────────────────────────────────────────────────

    [Fact]
    public void Report_Catalog_Events_Are_Serialised_With_Success_Flag()
    {
        var evt    = new CatalogEvent("web-feed", "europe", true, "[1.0.0]  2 VM(s)", null);
        Reporter.Report(EmptyReport() with { CatalogEvents = [evt] });

        using var doc  = JsonDocument.Parse(Output);
        var events = doc.RootElement.GetProperty("catalog_events").EnumerateArray().ToList();
        events.Should().HaveCount(1);
        events[0].GetProperty("success").GetBoolean().Should().BeTrue();
        events[0].GetProperty("service").GetString().Should().Be("web-feed");
        events[0].GetProperty("region").GetString().Should().Be("europe");
    }
}
