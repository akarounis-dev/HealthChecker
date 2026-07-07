using FluentAssertions;
using HealthChecker.Application;
using HealthChecker.Models;
using Xunit;

namespace HealthChecker.Tests.Unit;

/// <summary>
/// Tests for ConfigValidator - validates that well-formed configs pass and
/// bad ones produce the right error messages without false positives.
/// </summary>
public class ConfigValidatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    static CatalogConfig ValidCatalog() =>
        new(BaseUrl: "https://catalog.example.com",
            Environment: "prd",
            KubernetesDomain: "novibet.systems");

    static RegionConfig CatalogRegion(string platform = "eur", string region = "euw") =>
        new(CatalogPlatform: platform, CatalogRegion: region);

    static ServiceConfig ValidK8sService(
        string  catalogName = "web-feed",
        string? alias       = null,
        string  region      = "europe") =>
        new(CatalogName:        catalogName,
            Port:               8080,
            HealthcheckContains:"ok",
            Regions:            new Dictionary<string, RegionConfig> { [region] = CatalogRegion() },
            Alias:              alias);

    static Config ValidConfig(params ServiceConfig[] services) =>
        new(Defaults: null,
            Services: services.Length > 0 ? services : [ValidK8sService()],
            Catalog:  ValidCatalog());

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Returns_No_Errors_For_Minimal_Valid_Config()
    {
        ConfigValidator.Validate(ValidConfig(ValidK8sService())).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Returns_No_Errors_For_Hardcoded_Vm_Targets_With_Platform_Region()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(
                    VmTargets: ["prd-spt01-01"],
                    CatalogPlatform: "eur",
                    CatalogRegion: "euw") });

        ConfigValidator.Validate(ValidConfig(svc)).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Returns_No_Errors_For_Hardcoded_Vm_Targets_With_VmUrlTemplate()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(
                    VmTargets: ["prd-spt01-01"],
                    VmUrlTemplate: "http://{target}.example.com:{port}/") });

        ConfigValidator.Validate(ValidConfig(svc)).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Reports_Error_For_Vm_Targets_Without_Platform_Region_Or_Template()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(VmTargets: ["prd-spt01-01"]) });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e =>
                e.Contains("vm_targets") &&
                (e.Contains("catalog_platform") || e.Contains("vm_url_template")));
    }

    [Fact]
    public void Validate_Returns_No_Errors_For_Hardcoded_K8s_Url()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(KubernetesUrl: "https://my-app.example.com/") });

        ConfigValidator.Validate(ValidConfig(svc)).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Returns_No_Errors_For_Multiple_Services_With_Distinct_Aliases()
    {
        ConfigValidator.Validate(ValidConfig(
            ValidK8sService("web-feed", alias: "feed"),
            ValidK8sService("web-sbi",  alias: "sbi", region: "brazil")
        )).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Returns_No_Errors_When_No_Catalog_Section_And_All_Regions_Hardcoded()
    {
        // Config without a catalog section is valid when vm_targets has an explicit
        // vm_url_template so the runner can construct URLs without catalog_platform/region.
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(
                    VmTargets:     ["prd-spt01-01"],
                    VmUrlTemplate: "http://{target}.example.com:{port}/health") });

        var config = new Config(Defaults: null, Services: [svc], Catalog: null);
        ConfigValidator.Validate(config).Should().BeEmpty();
    }

    // ── services[] ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_Reports_Error_When_Services_Is_Empty()
    {
        var config = new Config(Defaults: null, Services: [], Catalog: ValidCatalog());
        ConfigValidator.Validate(config)
            .Should().ContainSingle(e => e.Contains("at least one entry"));
    }

    // ── Duplicate alias detection ─────────────────────────────────────────────

    [Fact]
    public void Validate_Reports_Error_For_Duplicate_Explicit_Aliases()
    {
        var errors = ConfigValidator.Validate(ValidConfig(
            ValidK8sService("web-feed", alias: "feed"),
            ValidK8sService("web-sbi",  alias: "feed", region: "brazil")));

        errors.Should().ContainSingle(e => e.Contains("Duplicate alias") && e.Contains("feed"));
    }

    [Fact]
    public void Validate_Reports_Error_For_Duplicate_CatalogName_Fallback_Aliases()
    {
        // Neither service sets alias → both alias to their catalog_name "web-feed"
        var errors = ConfigValidator.Validate(ValidConfig(
            ValidK8sService("web-feed"),
            ValidK8sService("web-feed", region: "brazil")));

        errors.Should().ContainSingle(e => e.Contains("Duplicate alias") && e.Contains("web-feed"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Explicit_Alias_Collides_With_Another_CatalogName_Alias()
    {
        // Service A: no alias → alias = "web-feed"
        // Service B: alias = "web-feed" → collision
        var errors = ConfigValidator.Validate(ValidConfig(
            ValidK8sService("web-feed"),
            ValidK8sService("web-sbi", alias: "web-feed", region: "brazil")));

        errors.Should().ContainSingle(e => e.Contains("Duplicate alias") && e.Contains("web-feed"));
    }

    [Fact]
    public void Validate_Reports_Error_For_Duplicate_Aliases_Case_Insensitively()
    {
        var errors = ConfigValidator.Validate(ValidConfig(
            ValidK8sService("web-feed", alias: "Feed"),
            ValidK8sService("web-sbi",  alias: "FEED", region: "brazil")));

        // Normalised to lowercase in the error message
        errors.Should().ContainSingle(e => e.Contains("Duplicate alias") && e.Contains("feed"));
    }

    [Fact]
    public void Validate_Reports_One_Error_Per_Duplicate_Alias_Not_One_Per_Offending_Service()
    {
        // Three services sharing the same alias → exactly one duplicate-alias error
        var errors = ConfigValidator.Validate(ValidConfig(
            ValidK8sService("web-a", alias: "shared"),
            ValidK8sService("web-b", alias: "shared", region: "brazil"),
            ValidK8sService("web-c", alias: "shared", region: "us")));

        errors.Where(e => e.Contains("Duplicate alias") && e.Contains("shared"))
              .Should().HaveCount(1);
    }

    // ── Per-service required fields ───────────────────────────────────────────

    [Fact]
    public void Validate_Reports_Error_When_CatalogName_Is_Empty()
    {
        var svc = new ServiceConfig(
            CatalogName:        "",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig> { ["europe"] = CatalogRegion() });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("catalog_name is required"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Port_Is_Zero()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               0,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig> { ["europe"] = CatalogRegion() });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("port must be a positive integer") && e.Contains("0"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Port_Is_Negative()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               -8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig> { ["europe"] = CatalogRegion() });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("port must be a positive integer"));
    }

    [Fact]
    public void Validate_Reports_Error_When_HealthcheckContains_Is_Empty()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"",
            Regions: new Dictionary<string, RegionConfig> { ["europe"] = CatalogRegion() });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("healthcheck_contains is required"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Regions_Is_Empty()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions:            []);

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("at least one region"));
    }

    // ── Per-region validation ─────────────────────────────────────────────────

    [Fact]
    public void Validate_Reports_Error_When_CatalogPlatform_Set_But_CatalogRegion_Missing()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(CatalogPlatform: "eur") });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("both be set or both omitted"));
    }

    [Fact]
    public void Validate_Reports_Error_When_CatalogRegion_Set_But_CatalogPlatform_Missing()
    {
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(CatalogRegion: "euw") });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e => e.Contains("both be set or both omitted"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Region_Has_No_Resolution_Method()
    {
        // No platform+region, no vm_targets, no kubernetes_url
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig() });

        ConfigValidator.Validate(ValidConfig(svc))
            .Should().Contain(e =>
                e.Contains("catalog_platform+catalog_region") &&
                e.Contains("vm_targets") &&
                e.Contains("kubernetes_url"));
    }

    [Fact]
    public void Validate_Returns_No_Errors_For_Hardcoded_K8s_Url_Without_Catalog_Section()
    {
        // A hardcoded kubernetes_url needs no catalog section - the URL is already resolved.
        var svc = new ServiceConfig(
            CatalogName:        "web-feed",
            Port:               8080,
            HealthcheckContains:"ok",
            Regions: new Dictionary<string, RegionConfig>
                { ["europe"] = new RegionConfig(KubernetesUrl: "https://my-app.example.com/") });

        var config = new Config(Defaults: null, Services: [svc], Catalog: null);
        ConfigValidator.Validate(config).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Reports_Error_When_Catalog_Resolution_Used_But_Catalog_Section_Missing()
    {
        var svc    = ValidK8sService("web-feed");
        var config = new Config(Defaults: null, Services: [svc], Catalog: null);

        ConfigValidator.Validate(config)
            .Should().Contain(e => e.Contains("'catalog' section is missing"));
    }

    // ── catalog section ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_Reports_Error_When_Catalog_BaseUrl_Is_Missing()
    {
        var catalog = new CatalogConfig(BaseUrl: "", Environment: "prd", KubernetesDomain: "novibet.systems");
        var config  = new Config(Defaults: null, Services: [ValidK8sService()], Catalog: catalog);

        ConfigValidator.Validate(config)
            .Should().Contain(e => e.Contains("catalog.base_url is required"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Catalog_Environment_Is_Missing()
    {
        var catalog = new CatalogConfig(BaseUrl: "https://catalog.example.com", Environment: "", KubernetesDomain: "novibet.systems");
        var config  = new Config(Defaults: null, Services: [ValidK8sService()], Catalog: catalog);

        ConfigValidator.Validate(config)
            .Should().Contain(e => e.Contains("catalog.environment is required"));
    }

    [Fact]
    public void Validate_Reports_Error_When_Catalog_K8sDomain_Is_Missing()
    {
        var catalog = new CatalogConfig(BaseUrl: "https://catalog.example.com", Environment: "prd", KubernetesDomain: "");
        var config  = new Config(Defaults: null, Services: [ValidK8sService()], Catalog: catalog);

        ConfigValidator.Validate(config)
            .Should().Contain(e => e.Contains("catalog.k8s_domain is required"));
    }

    // ── Multi-error accumulation ──────────────────────────────────────────────

    [Fact]
    public void Validate_Collects_All_Errors_In_A_Single_Pass()
    {
        var badSvc = new ServiceConfig(
            CatalogName:        "",
            Port:               0,
            HealthcheckContains:"",
            Regions:            []);

        var errors = ConfigValidator.Validate(
            new Config(Defaults: null, Services: [badSvc], Catalog: ValidCatalog()));

        errors.Should().Contain(e => e.Contains("catalog_name is required"));
        errors.Should().Contain(e => e.Contains("port must be a positive integer"));
        errors.Should().Contain(e => e.Contains("healthcheck_contains is required"));
        errors.Should().Contain(e => e.Contains("at least one region"));
        errors.Count.Should().BeGreaterThan(1);
    }
}
