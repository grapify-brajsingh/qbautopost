using Microsoft.Extensions.Options;
using QbAutopost.Api.Companies;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Tests.Companies;

public sealed class CompanyResolverTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);

    private readonly TempDir _dir = new();
    private readonly FakeCompanyStore _store = new();
    private readonly AppSettings _settings;
    private readonly CompanyDataFolder _folders;

    public CompanyResolverTests()
    {
        _settings = new AppSettings
        {
            Company = { Name = "Tropicana", FilePath = _dir.Combine("qb", "Tropicana.qbw"), RulesFile = _dir.Combine("rules.json") },
            Paths = { Ledger = _dir.Combine("ledger.json") },
            Companies = { RootFolder = _dir.Combine("root"), DataFolder = _dir.Combine("companies") },
        };
        _folders = new CompanyDataFolder(Options.Create(_settings));
    }

    public void Dispose() => _dir.Dispose();

    private CompanyResolver Resolver(bool withStore = true) =>
        new(Options.Create(_settings), _folders, withStore ? _store : null);

    private Company Scanned(int id, CompanyState state = CompanyState.Active, string? file = "x.qbw") =>
        new(id, $"{id}_Co", $"{id}", $"Co {id}", file, [], state, CompanyAccess.Approved, Now, null, null, null, [], false, Now, Now, 1);

    private async Task<CompanyResolver> WithDefaultAsync()
    {
        var resolver = Resolver();
        var boot = await _store.EnsureDefaultAsync(new NewDefaultCompany("Tropicana", _settings.Company.FilePath, null, null), Now, default);
        resolver.UseDefaultId(boot.Company.Id);
        return resolver;
    }

    [Fact]
    public async Task Should_UseConfiguredFiles_When_ResolvingTheDefaultCompany()
    {
        var resolver = await WithDefaultAsync();

        var context = await resolver.GetAsync(1, default);

        Assert.Equal(
            (1, _settings.Company.FilePath, _settings.Company.RulesFile, _settings.Paths.Ledger, true),
            (context!.Id, context.FilePath, context.RulesFile, context.LedgerFile, context.IsDefault));
    }

    [Fact]
    public async Task Should_UseDataFolderFiles_When_ResolvingAScannedCompany()
    {
        var resolver = await WithDefaultAsync();
        _store.Add(Scanned(2));

        var context = await resolver.GetAsync(2, default);

        Assert.Equal(
            (_dir.Combine("companies", "2", "rules.json"), _dir.Combine("companies", "2", "ledger.json"), "x.qbw", false),
            (context!.RulesFile, context.LedgerFile, context.FilePath, context.IsDefault));
    }

    [Fact]
    public async Task Should_CarryStateAndUseEmptyFile_When_ScannedCompanyHasNoFile()
    {
        var resolver = await WithDefaultAsync();
        _store.Add(Scanned(2, CompanyState.Ambiguous, file: null));

        var context = await resolver.GetAsync(2, default);

        Assert.Equal(("", CompanyState.Ambiguous, false), (context!.FilePath, context.State, context.CanOpenSession));
    }

    [Fact]
    public async Task Should_AutoAssignDefault_When_RootFolderIsEmpty()
    {
        _settings.Companies.RootFolder = "";
        var resolver = await WithDefaultAsync();
        _store.Add(Scanned(2));

        var assigned = await resolver.AutoAssignAsync(default);

        Assert.False(resolver.MultiCompany);
        Assert.True(assigned!.IsDefault);
    }

    [Fact]
    public async Task Should_AutoAssignTheOnlyActiveCompany_When_OneIsActive()
    {
        var resolver = Resolver();
        resolver.UseDefaultId(1);
        _store.Add(Scanned(2));
        _store.Add(Scanned(3, CompanyState.Missing));

        var assigned = await resolver.AutoAssignAsync(default);

        Assert.Equal(2, assigned!.Id);
    }

    [Fact]
    public async Task Should_AutoAssignNothing_When_TwoCompaniesAreActive()
    {
        var resolver = Resolver();
        resolver.UseDefaultId(1);
        _store.Add(Scanned(2));
        _store.Add(Scanned(3));

        Assert.Null(await resolver.AutoAssignAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Should_HandOutNoContext_When_DefaultIdIsNotKnownYet(bool rootFolderSet)
    {
        if (!rootFolderSet)
        {
            _settings.Companies.RootFolder = "";
        }

        var resolver = Resolver();
        _store.Add(Scanned(2));

        Assert.Equal((false, null), (resolver.HasDefault, await resolver.AutoAssignAsync(default)));
    }

    [Fact]
    public async Task Should_HandOutNoDefaultContext_When_NoDatabaseAndDefaultIdIsNotKnownYet()
    {
        var resolver = Resolver(withStore: false);

        Assert.Null(await resolver.GetAsync(0, default));
    }

    [Fact]
    public async Task Should_ReportSingleCompany_When_NoDatabase()
    {
        var resolver = Resolver(withStore: false);
        resolver.UseDefaultId(1);

        Assert.False(resolver.MultiCompany);
        Assert.Equal(1, (await resolver.GetAsync(1, default))!.Id);
        Assert.Null(await resolver.GetAsync(2, default));
    }
}
