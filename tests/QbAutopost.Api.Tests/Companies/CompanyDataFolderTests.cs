using Microsoft.Extensions.Options;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Companies;

public sealed class CompanyDataFolderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _template;
    private readonly CompanyDataFolder _folders;

    public CompanyDataFolderTests()
    {
        _template = _dir.Combine("template.json");
        File.Copy(Fixtures.SampleRules, _template);
        var settings = new AppSettings { Companies = { DataFolder = _dir.Combine("companies"), RulesTemplate = _template } };
        _folders = new CompanyDataFolder(Options.Create(settings));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Should_CopyTemplateAndCreateEmptyLedger_When_Prepared()
    {
        _folders.Prepare(7);

        Assert.Equal(_dir.Combine("companies", "7", "rules.json"), _folders.RulesFileOf(7));
        Assert.Equal(File.ReadAllText(_template), File.ReadAllText(_folders.RulesFileOf(7)));
        Assert.Empty(new LedgerStore(_folders.LedgerFileOf(7)).Load().Posted);
        Assert.True(File.Exists(_folders.LedgerFileOf(7)));
    }

    [Fact]
    public void Should_KeepEditedRules_When_PreparedAgain()
    {
        _folders.Prepare(7);
        File.WriteAllText(_folders.RulesFileOf(7), "{\"edited\":true}");
        File.WriteAllText(_template, "{\"template\":\"changed\"}");

        _folders.Prepare(7);

        Assert.Equal("{\"edited\":true}", File.ReadAllText(_folders.RulesFileOf(7)));
    }
}
