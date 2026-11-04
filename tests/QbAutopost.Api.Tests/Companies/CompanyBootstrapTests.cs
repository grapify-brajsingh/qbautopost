using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Tests.Companies;

public sealed class CompanyBootstrapTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeCompanyStore _store = new();
    private readonly AppSettings _settings = new();
    private readonly CompanyResolver _resolver;
    private readonly CompanyBootstrap _bootstrap;

    public CompanyBootstrapTests()
    {
        _settings.Company.Name = "Takoma Park Grocery Store Inc";
        _settings.Company.FilePath = _dir.Combine("elsewhere", "Takoma.qbw");
        _settings.Company.RulesFile = Fixtures.SampleRules;
        _settings.Paths.Ledger = _dir.Combine("ledger.json");
        _settings.Companies.DataFolder = _dir.Combine("companies");
        _settings.Companies.RulesTemplate = Fixtures.SampleRules;
        var options = Options.Create(_settings);
        _resolver = new CompanyResolver(options, new CompanyDataFolder(options), _store);
        _bootstrap = new CompanyBootstrap(_store, options, _resolver, new FixedClock(), NullLogger<CompanyBootstrap>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Should_CreateDefaultFromCompanySettings_When_NoCompanyExists()
    {
        var result = await _bootstrap.RunAsync(default);

        Assert.Equal(
            ("Takoma Park Grocery Store Inc", _settings.Company.FilePath, CompanyState.Active, CompanyAccess.Approved, true, true),
            (result.Company.DisplayName, result.Company.FilePath, result.Company.State, result.Company.Access, result.Company.IsDefault, result.Created));
        Assert.Equal(result.Company.Id, _resolver.Default.Id);
    }

    [Fact]
    public async Task Should_SetFolderNameAndCode_When_DefaultFileLiesUnderRootFolder()
    {
        _settings.Companies.RootFolder = _dir.Combine("root");
        _settings.Company.FilePath = _dir.Combine("root", "18_Takoma Park Grocery Store Inc", "Takoma.qbw");

        var company = (await _bootstrap.RunAsync(default)).Company;

        Assert.Equal(("18_Takoma Park Grocery Store Inc", "18"), (company.FolderName, company.Code));
    }

    [Fact]
    public async Task Should_LeaveFolderNameEmpty_When_DefaultFileIsOutsideRootFolder()
    {
        _settings.Companies.RootFolder = _dir.Combine("root");

        var company = (await _bootstrap.RunAsync(default)).Company;

        Assert.Equal((null, null), (company.FolderName, company.Code));
    }

    [Fact]
    public async Task Should_CopyNoFiles_When_CreatingTheDefault()
    {
        await _bootstrap.RunAsync(default);

        Assert.False(Directory.Exists(_settings.Companies.DataFolder) && Directory.EnumerateFileSystemEntries(_settings.Companies.DataFolder).Any());
        Assert.False(File.Exists(_settings.Paths.Ledger));
    }

    [Fact]
    public async Task Should_NameTheDefaultCompany_When_CompanyNameIsEmpty()
    {
        _settings.Company.Name = "";

        var company = (await _bootstrap.RunAsync(default)).Company;

        Assert.Equal("Default company", company.DisplayName);
    }

    [Fact]
    public async Task Should_LeaveResolverWithoutDefault_When_RunFails()
    {
        var options = Options.Create(_settings);
        var failing = new CompanyBootstrap(new FailingStore(), options, _resolver, new FixedClock(), NullLogger<CompanyBootstrap>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunAsync(default));

        Assert.False(_resolver.HasDefault);
        Assert.Null(await _resolver.AutoAssignAsync(default));
    }

    private sealed class FailingStore : ICompanyStore
    {
        public Task<DefaultCompanyResult> EnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct) =>
            throw new InvalidOperationException("database down");

        public Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<Company?> GetAsync(int id, CancellationToken ct) => throw new NotSupportedException();

        public Task<ScanApplied> ApplyScanAsync(ScanPlan plan, string actor, DateTime nowUtc, CancellationToken ct) => throw new NotSupportedException();

        public Task<CompanyChange> UpdateAsync(int id, int expectedRowVersion, CompanyPatch patch, string actor, DateTime nowUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CompanyChange> RecordAccessAsync(int id, CompanyAccessCheck check, string actor, CancellationToken ct) => throw new NotSupportedException();

        public Task<DateTime?> LastScanUtcAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Should_DoNothing_When_RunTwice()
    {
        var first = await _bootstrap.RunAsync(default);
        var second = await _bootstrap.RunAsync(default);

        Assert.Equal((true, false, first.Company.Id, 1), (first.Created, second.Created, second.Company.Id, (await _store.ListAsync(default)).Count));
    }
}
