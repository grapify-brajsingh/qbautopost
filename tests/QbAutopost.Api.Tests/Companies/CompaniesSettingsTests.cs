using QbAutopost.Api.Configuration;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Companies;

/// <summary>M17 §4.1: the <c>Companies</c> section, its path resolution and its start-up checks.</summary>
public sealed class CompaniesSettingsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "qbautopost-root");

    private static string Validate(AppSettings settings)
    {
        var result = new CompaniesOptionsValidator().Validate(null, settings);
        return result.Failed ? result.FailureMessage : "";
    }

    [Fact]
    public void Should_DefaultToWaitingOnlyAndDataCompanies_When_SectionAbsent()
    {
        var c = new AppSettings().Companies;

        Assert.Equal(("", "data/companies", "data/rules.json", "waiting-only", false),
            (c.RootFolder, c.DataFolder, c.RulesTemplate, c.ScheduledSync, c.Configured));
    }

    [Fact]
    public void Should_ResolveDataFolderAndTemplate_When_PathsAreRelative()
    {
        var absoluteRoot = Path.Combine(Path.GetTempPath(), "companies-root");
        var settings = new AppSettings { Companies = { RootFolder = absoluteRoot } };

        settings.ResolvePaths(Root);

        Assert.Equal(
            (Path.Combine(Root, "data", "companies"), Path.Combine(Root, "data", "rules.json"), absoluteRoot),
            (settings.Companies.DataFolder, settings.Companies.RulesTemplate, settings.Companies.RootFolder));
    }

    [Theory]
    [InlineData("relative\\folder")]
    [InlineData("data/companies")]
    public void Should_FailStartup_When_RootFolderIsNotAbsolute(string root)
    {
        using var dir = new TempDir();
        var settings = new AppSettings { Companies = { RootFolder = root, RulesTemplate = Fixtures.SampleRules } };

        Assert.Contains("Companies:RootFolder", Validate(settings));
    }

    [Fact]
    public void Should_FailStartup_When_ScheduledSyncIsUnknown()
    {
        var message = Validate(new AppSettings { Companies = { ScheduledSync = "sometimes" } });

        Assert.Contains("Companies:ScheduledSync", message);
        Assert.All(new[] { "waiting-only", "all", "off" }, v => Assert.Contains(v, message));
    }

    [Fact]
    public void Should_FailStartup_When_RootFolderSetAndRulesTemplateMissing()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { Companies = { RootFolder = dir.Path, RulesTemplate = dir.Combine("nope.json") } };

        Assert.Contains("Companies:RulesTemplate", Validate(settings));
    }

    [Fact]
    public void Should_Pass_When_RootFolderIsEmptyAndTemplateMissing()
    {
        var settings = new AppSettings { Companies = { RulesTemplate = Path.Combine(Root, "nope.json") } };

        Assert.Equal("", Validate(settings));
    }
}
