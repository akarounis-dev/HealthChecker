using FluentAssertions;
using HealthChecker.Models;
using HealthChecker.Output;
using Spectre.Console;
using Xunit;

namespace HealthChecker.Tests.Unit;

/// <summary>
/// Tests ConsoleReporter output by injecting a StringWriter-backed IAnsiConsole.
/// We verify observable behavior (what gets printed) rather than testing private internals.
/// </summary>
public class ReporterTests : IDisposable
{
    private readonly StringWriter _out = new();

    public void Dispose() => _out.Dispose();

    string Output => _out.ToString();

    /// <summary>
    /// Creates a ConsoleReporter that writes to <see cref="_out"/>.
    /// noColor=true (default) produces plain text; noColor=false emits ANSI SGR codes.
    /// </summary>
    ConsoleReporter MakeReporter(bool noColor = true)
    {
        var settings = new AnsiConsoleSettings
        {
            Out         = new AnsiConsoleOutput(_out),
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Standard,
        };
        // Pass _out as the rawOut writer so Console.Out-bypass lines (e.g. Kibana URLs) are also captured.
        return new ConsoleReporter(AnsiConsole.Create(settings), _out);
    }

    ConsoleReporter Reporter => MakeReporter();

    // ── Kibana URL - data view ID resolution priority ─────────────────────────

    [Fact]
    public void Report_Shows_Kibana_Url_With_Region_Level_DataViewId()
    {
        var report = MakeFailedReport(
            catalogName: "web-feed", region: "europe",
            regionDataViewId: "region-id-111", kibanaGlobalId: "global-id");

        Reporter.Report(report);

        Output.Should().Contain("region-id-111");
        Output.Should().NotContain("global-id");
    }

    [Fact]
    public void Report_Falls_Back_To_Service_Level_DataViewId()
    {
        var report = MakeFailedReport(
            catalogName: "web-feed", region: "europe",
            regionDataViewId: null, serviceDataViewId: "service-id-222", kibanaGlobalId: "global-id");

        Reporter.Report(report);

        Output.Should().Contain("service-id-222");
        Output.Should().NotContain("global-id");
    }

    [Fact]
    public void Report_Falls_Back_To_Global_DataViewId()
    {
        var report = MakeFailedReport(
            catalogName: "web-feed", region: "europe",
            regionDataViewId: null, serviceDataViewId: null, kibanaGlobalId: "global-id-333");

        Reporter.Report(report);

        Output.Should().Contain("global-id-333");
    }

    [Fact]
    public void Report_Shows_No_DataViewId_Warning_When_All_Null()
    {
        var report = MakeFailedReport(
            catalogName: "web-feed", region: "europe",
            regionDataViewId: null, serviceDataViewId: null, kibanaGlobalId: null);

        Reporter.Report(report);

        Output.Should().Contain("no kibana_data_view_id configured");
        Output.Should().NotContain("kibana.dvo-novibet.systems/app/discover");
    }

    // ── Kibana URL - query and time window ────────────────────────────────────

    [Fact]
    public void Report_Kibana_Url_Contains_Log_Minutes()
    {
        var report = MakeFailedReport("web-feed", "europe", "some-id",
            kibana: MakeKibana("some-id", logMinutes: 10));

        Reporter.Report(report);

        Output.Should().Contain("now-10m");
    }

    [Fact]
    public void Report_Kibana_Url_Replaces_Service_Placeholder_In_Query()
    {
        var report = MakeFailedReport("web-sportsbookfeed", "europe", "some-id",
            kibana: MakeKibana("some-id", queryTemplate: "app: \"{service}\" AND level: \"Error\""));

        Reporter.Report(report);

        Output.Should().Contain("app: \"web-sportsbookfeed\"");
    }

    [Fact]
    public void Report_Escapes_Single_Quotes_In_Query()
    {
        var report = MakeFailedReport("web-feed", "europe", "some-id",
            kibana: MakeKibana("some-id", queryTemplate: "it's a query"));

        Reporter.Report(report);

        Output.Should().Contain("%27");
    }

    // ── Healthy checks - no Kibana link ──────────────────────────────────────

    [Fact]
    public void Report_Does_Not_Show_Kibana_Url_For_Healthy_Check()
    {
        var svc    = MakeService("web-feed", dataViewId: "some-id");
        var result = new CheckResult("http://ok/", true, 100, null);
        var entry  = new CheckEntry(svc, "europe", result);

        var report = EmptyReport() with
        {
            HttpResults = [entry],
            Kibana      = MakeKibana("some-id")
        };

        Reporter.Report(report);

        Output.Should().NotContain("kibana");
    }

    // ── ArgoCD results ────────────────────────────────────────────────────────

    [Fact]
    public void Report_Shows_Investigate_Link_When_Unhealthy()
    {
        var report = EmptyReport() with
        {
            HttpResults  = [MakeHealthyEntry()],
            ArgocdResults =
            [
                new ArgocdEntry("europe", "web-feed", "web-feed-eur-euw-prd",
                    "https://argo.example.com/applications/web-feed-eur-euw-prd",
                    "Degraded", ["Deployment/web-feed [Degraded]"], 2)
            ]
        };

        Reporter.Report(report);

        Output.Should().Contain("Degraded");
        Output.Should().Contain("Investigate");
        Output.Should().Contain("https://argo.example.com/applications/web-feed-eur-euw-prd");
    }

    [Fact]
    public void Report_Does_Not_Show_Investigate_Link_When_Healthy()
    {
        var report = EmptyReport() with
        {
            HttpResults   = [MakeHealthyEntry()],
            ArgocdResults =
            [
                new ArgocdEntry("europe", "web-feed", "web-feed-eur-euw-prd",
                    "https://argo.example.com/applications/web-feed-eur-euw-prd",
                    "Healthy", [], 3)
            ]
        };

        Reporter.Report(report);

        Output.Should().Contain("Healthy");
        Output.Should().NotContain("Investigate");
    }

    [Fact]
    public void Report_Shows_Pod_Count()
    {
        var report = EmptyReport() with
        {
            HttpResults   = [MakeHealthyEntry()],
            ArgocdResults =
            [
                new ArgocdEntry("europe", "web-feed", "app", "http://argo/", "Healthy", [], 5)
            ]
        };

        Reporter.Report(report);

        Output.Should().Contain("5 pod(s)");
    }

    [Fact]
    public void Report_Shows_Unknown_Pod_Count_When_Zero()
    {
        var report = EmptyReport() with
        {
            HttpResults   = [MakeHealthyEntry()],
            ArgocdResults =
            [
                new ArgocdEntry("europe", "web-feed", "app", "http://argo/", "Healthy", [], 0)
            ]
        };

        Reporter.Report(report);

        Output.Should().Contain("? pods");
    }

    // ── Response time threshold (WN indicator) ───────────────────────────────

    [Fact]
    public void Report_Shows_WN_Indicator_For_Slow_Healthy_Check()
    {
        var svc    = MakeService("web-feed");
        var result = new CheckResult("http://slow/", true, 800, null);
        var entry  = new CheckEntry(svc, "europe", result);

        var report = EmptyReport() with
        {
            HttpResults        = [entry],
            ResponseTimeWarnMs = 500
        };

        Reporter.Report(report);

        Output.Should().Contain("WN");
        Output.Should().NotContain("OK");
    }

    [Fact]
    public void Report_Shows_OK_Indicator_For_Fast_Healthy_Check_Even_With_Threshold_Set()
    {
        var svc    = MakeService("web-feed");
        var result = new CheckResult("http://fast/", true, 100, null);
        var entry  = new CheckEntry(svc, "europe", result);

        var report = EmptyReport() with
        {
            HttpResults        = [entry],
            ResponseTimeWarnMs = 500
        };

        Reporter.Report(report);

        Output.Should().Contain("OK");
        Output.Should().NotContain("WN");
    }

    [Fact]
    public void Report_Shows_Slow_Count_In_Summary_When_Threshold_Set()
    {
        var svc   = MakeService("web-feed");
        var report = EmptyReport() with
        {
            HttpResults =
            [
                new CheckEntry(svc, "europe", new CheckResult("http://fast/", true, 100, null)),
                new CheckEntry(svc, "europe", new CheckResult("http://slow/", true, 900, null)),
            ],
            ResponseTimeWarnMs = 500
        };

        Reporter.Report(report);

        Output.Should().Contain("slow");
        Output.Should().Contain(">500ms");
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    [Fact]
    public void Report_Shows_All_Healthy_Message_When_No_Failures()
    {
        var entries = Enumerable.Range(0, 5)
            .Select(i => MakeHealthyEntry($"http://svc-{i}/"))
            .ToList();

        var report = EmptyReport() with { HttpResults = entries };

        Reporter.Report(report);

        Output.Should().Contain("All 5 instance(s) healthy");
    }

    [Fact]
    public void Report_Shows_Partial_Failure_Count()
    {
        var entries = new List<CheckEntry>
        {
            MakeHealthyEntry("http://ok1/"),
            MakeHealthyEntry("http://ok2/"),
            MakeHealthyEntry("http://ok3/"),
            MakeFailedEntry("web-feed", "europe"),
            MakeFailedEntry("web-feed", "europe", url: "http://fail2/")
        };

        var report = EmptyReport() with { HttpResults = entries };

        Reporter.Report(report);

        Output.Should().Contain("3 / 5");
        Output.Should().Contain("2 failed");
    }

    // ── BuildKibanaUrl - URL structure (tested directly on the internal method) ──

    [Fact]
    public void BuildKibanaUrl_Returns_Null_When_No_Id_At_Any_Level()
    {
        ConsoleReporter.BuildKibanaUrl(MakeKibana(globalId: null), "web-feed", null, null)
            .Should().BeNull();
    }

    [Fact]
    public void BuildKibanaUrl_Prefers_Region_Over_Service_And_Global()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("global"), "web-feed", "region-id", "service-id");

        url.Should().Contain("region-id");
        url.Should().NotContain("service-id");
        url.Should().NotContain("global");
    }

    [Fact]
    public void BuildKibanaUrl_Uses_Service_Id_When_Region_Id_Is_Null()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("global"), "web-feed", null, "service-id");

        url.Should().Contain("service-id");
        url.Should().NotContain("global");
    }

    [Fact]
    public void BuildKibanaUrl_Starts_With_Kibana_Base_And_Discover_Path()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("some-id"), "web-feed", null, null);

        url.Should().StartWith("https://kibana.dvo-novibet.systems/app/discover#/?");
    }

    [Fact]
    public void BuildKibanaUrl_Embeds_DataViewId_In_DataSource_Parameter()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("abc-123"), "web-feed", null, null);

        // The data view ID must appear inside the dataSource RISON parameter
        url.Should().Contain("dataViewId:'abc-123'");
    }

    [Fact]
    public void BuildKibanaUrl_Sets_Time_Range_From_Log_Minutes()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("id", logMinutes: 15), "web-feed", null, null);

        url.Should().Contain("from:now-15m");
        url.Should().Contain("to:now");
    }

    [Fact]
    public void BuildKibanaUrl_Substitutes_Service_Name_In_Query()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("id", queryTemplate: "app:\"{service}\" AND level:\"Error\""),
            "web-sportsbookfeed", null, null);

        url.Should().Contain("web-sportsbookfeed");
        url.Should().NotContain("{service}");
    }

    [Fact]
    public void BuildKibanaUrl_Escapes_Single_Quotes_In_Query()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("id", queryTemplate: "app:'web-feed'"),
            "web-feed", null, null);

        // Single quotes inside the RISON query string must be percent-encoded
        url.Should().Contain("%27web-feed%27");
        url.Should().NotMatchRegex(@"query:'app:'");  // raw unescaped inner quote would break RISON
    }

    [Fact]
    public void BuildKibanaUrl_Places_Query_Inside_Query_Parameter()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("id", queryTemplate: "level:\"Error\""),
            "web-feed", null, null);

        // Must be inside query:(language:kuery,query:'...')
        url.Should().Contain("query:(language:kuery,query:'level:\"Error\"')");
    }

    [Fact]
    public void BuildKibanaUrl_Full_Url_Contains_All_Required_Rison_Segments()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            new KibanaConfig(
                Url:           "https://kibana.example.com",
                DataViewId:    "view-abc",
                LogMinutes:    5,
                QueryTemplate: "level:\"Error\""),
            "my-service", null, null)!;

        // Base
        url.Should().StartWith("https://kibana.example.com/app/discover#/?");
        // Global time filter
        url.Should().Contain("_g=");
        url.Should().Contain("from:now-5m");
        // App state
        url.Should().Contain("_a=");
        url.Should().Contain("dataViewId:'view-abc'");
        url.Should().Contain("language:kuery");
        url.Should().Contain("level:\"Error\"");
    }

    // ── BuildKibanaUrl - instance (host.name) scoping ────────────────────────

    [Fact]
    public void BuildKibanaUrl_Appends_HostName_Filter_When_InstanceName_Provided()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("some-id", queryTemplate: "level: \"Error\""),
            "web-feed", null, null,
            instanceName: "dev-trd01-01");

        url.Should().Contain("host.name: \"dev-trd01-01\"");
        url.Should().Contain("level: \"Error\"");  // original query still present
    }

    [Fact]
    public void BuildKibanaUrl_Does_Not_Append_HostName_Filter_When_InstanceName_Is_Null()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("some-id"), "web-feed", null, null, instanceName: null);

        url.Should().NotContain("host.name");
    }

    // ── BuildKibanaUrl - columns ─────────────────────────────────────────────

    [Fact]
    public void BuildKibanaUrl_Uses_Empty_Columns_When_None_Configured()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("some-id"), "web-feed", null, null);

        url.Should().Contain("columns:!()");
    }

    [Fact]
    public void BuildKibanaUrl_Includes_Configured_Columns_In_Rison()
    {
        var url = ConsoleReporter.BuildKibanaUrl(
            MakeKibana("some-id", columns: ["level", "host.name", "message", "exceptionType"]),
            "web-feed", null, null);

        url.Should().Contain("columns:!(level,host.name,message,exceptionType)");
        url.Should().NotContain("columns:!()");
    }

    // ── ExtractVmInstanceName ────────────────────────────────────────────────

    [Theory]
    [InlineData("http://web-sportsbookfeed.dev-trd01-01.cen-sec.dev-novibet.systems:8092/", "dev-trd01-01")]
    [InlineData("http://web-sportsbookfeed.prd-spt09-02.eur-euw.prd-novibet.systems:8092/", "prd-spt09-02")]
    public void ExtractVmInstanceName_Returns_Target_For_VM_Urls(string url, string expected)
    {
        ConsoleReporter.ExtractVmInstanceName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("http://web-sportsbookfeed.eur-sec.dev-novibet.systems/")]   // k8s - no numeric suffix
    [InlineData("http://localhost:1/")]                                        // bare host
    [InlineData("not-a-url")]                                                  // invalid
    public void ExtractVmInstanceName_Returns_Null_For_Non_VM_Urls(string url)
    {
        ConsoleReporter.ExtractVmInstanceName(url).Should().BeNull();
    }

    // ── IsDnsError ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("No such host is known.")]
    [InlineData("Name or service not known")]
    [InlineData("nodename nor servname provided, or not known")]
    [InlineData("host not found")]
    [InlineData("NO SUCH HOST IS KNOWN.")]   // case-insensitive
    public void IsDnsError_Returns_True_For_Dns_Errors(string error)
    {
        ConsoleReporter.IsDnsError(error).Should().BeTrue();
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("Timeout after 5s")]
    [InlineData("Unexpected response (503): \"\"")]
    public void IsDnsError_Returns_False_For_Non_Dns_Errors(string error)
    {
        ConsoleReporter.IsDnsError(error).Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static HealthCheckReport EmptyReport() => new(
        ConfigPath:    "configs/config.test.json",
        FilterRegion:  null,
        CatalogEvents: [],
        HttpResults:   [],
        ArgocdResults: [],
        TokenWarning:  null,
        NoTokenMessage:null,
        Kibana:        null);

    static KibanaConfig MakeKibana(
        string? globalId = "default-id",
        int logMinutes = 5,
        string queryTemplate = "level: \"Error\"",
        string[]? columns = null)
        => new(Url: "https://kibana.dvo-novibet.systems",
               DataViewId: globalId,
               LogMinutes: logMinutes,
               QueryTemplate: queryTemplate,
               Columns: columns);

    static ServiceConfig MakeService(
        string catalogName,
        string? dataViewId = null,
        Dictionary<string, RegionConfig>? regions = null)
        => new(CatalogName:        catalogName,
               Port:               8080,
               HealthcheckContains:"ok",
               Regions:            regions ?? new Dictionary<string, RegionConfig>
                                       { ["europe"] = new RegionConfig() },
               KibanaDataViewId:   dataViewId);

    static CheckEntry MakeHealthyEntry(string url = "http://ok/")
    {
        var svc = MakeService("web-feed");
        return new CheckEntry(svc, "europe", new CheckResult(url, true, 100, null));
    }

    static CheckEntry MakeFailedEntry(
        string catalogName, string region,
        string? url = null,
        string? regionDataViewId = null,
        string? serviceDataViewId = null)
    {
        var regions = new Dictionary<string, RegionConfig>
            { [region] = new RegionConfig(KibanaDataViewId: regionDataViewId) };
        var svc     = MakeService(catalogName, serviceDataViewId, regions);
        return new CheckEntry(svc, region,
            new CheckResult(url ?? $"http://{catalogName}/", false, 0, "connection refused"));
    }

    HealthCheckReport MakeFailedReport(
        string catalogName, string region,
        string? regionDataViewId = null,
        string? serviceDataViewId = null,
        string? kibanaGlobalId = "default-id",
        KibanaConfig? kibana = null)
    {
        var entry = MakeFailedEntry(catalogName, region, null, regionDataViewId, serviceDataViewId);
        return EmptyReport() with
        {
            HttpResults = [entry],
            Kibana      = kibana ?? MakeKibana(kibanaGlobalId)
        };
    }

    // ── Token warnings ────────────────────────────────────────────────────────

    [Fact]
    public void Report_Shows_TokenWarning_When_Set()
    {
        var report = EmptyReport() with
        {
            HttpResults  = [MakeHealthyEntry()],
            TokenWarning = "ArgoCD token expires in 2h - run: argocd login ..."
        };

        Reporter.Report(report);

        Output.Should().Contain("ArgoCD token expires in 2h");
    }

    [Fact]
    public void Report_Shows_NoTokenMessage_When_Set()
    {
        var report = EmptyReport() with
        {
            HttpResults     = [MakeHealthyEntry()],
            NoTokenMessage  = "No ArgoCD token found.\nRun: argocd login <server>"
        };

        Reporter.Report(report);

        Output.Should().Contain("No ArgoCD token found.");
        Output.Should().Contain("argocd login");
    }

    // ── NoColor flag ──────────────────────────────────────────────────────────

    [Fact]
    public void Report_Produces_Correct_Text_Content_When_NoColor_Is_True()
    {
        var report = EmptyReport() with { HttpResults = [MakeHealthyEntry()] };
        MakeReporter(noColor: true).Report(report);

        // The summary text must still be present even with colours suppressed.
        Output.Should().Contain("All 1 instance(s) healthy");
    }

    [Fact]
    public void Report_Produces_Same_Text_Regardless_Of_NoColor_Setting()
    {
        var entry  = MakeHealthyEntry();
        var report = EmptyReport() with { HttpResults = [entry] };

        MakeReporter(noColor: false).Report(report);
        var outputWithColor = Output;

        _out.GetStringBuilder().Clear();

        MakeReporter(noColor: true).Report(report);
        var outputNoColor = Output;

        // Strip all ANSI escape sequences (both SGR color codes and OSC 8 hyperlinks)
        // before comparing, so we assert on the readable text only.
        StripAllAnsi(outputWithColor).Should().Be(outputNoColor);
    }

    /// <summary>
    /// Strips all ANSI escape sequences:
    ///   • SGR codes:  ESC [ ... m
    ///   • OSC 8:      ESC ] 8 ;; url ESC \
    ///   • Other CSI:  ESC [ ... (letter)
    /// </summary>
    static string StripAllAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\x1b(?:\[[^a-zA-Z]*[a-zA-Z]|\][^\x1b]*(?:\x1b\\|$))",
            "");
}
