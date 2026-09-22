using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>
/// T-905 / FR-A-5: is this the right, usable, recently-backed-up company file? Read-only — nothing is posted, and the
/// stub gateways below throw on <c>ProcessAsync</c> to prove no qbXML is sent. The check that matters most is
/// <c>openInQuickBooks</c>: QuickBooks having a *different* file open is the most dangerous misconfiguration this app
/// has, so it is an error, never a warning.
/// </summary>
public sealed class CompanyFileValidatorTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string CompanyFile(string name = "Tropicana.QBW")
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, "not a real company file");
        return path;
    }

    private CompanyFileValidator Build(IQbGateway gateway, string? backupFolder = null, int backupMaxAgeHours = 36) =>
        new(
            new PipelineOptions
            {
                CompanyName = ApiFactory.Company,
                RulesFile = _dir.Combine("rules.json"),
                LedgerFile = _dir.Combine("ledger.json"),
                QbListsFile = _dir.Combine("qb-lists.json"),
                BackupFolder = backupFolder ?? string.Empty,
                BackupMaxAgeHours = backupMaxAgeHours,
            },
            gateway);

    private static CompanyFileCheck Check(CompanyFileValidation result, string name) =>
        Assert.Single(result.Checks, c => c.Name == name);

    [Fact]
    public async Task Should_BeOk_When_QuickBooksHasTheConfiguredFileOpen()
    {
        var file = CompanyFile();
        var validator = Build(new StubGateway(file));

        var result = await validator.ValidateAsync(file, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(file, result.CompanyFile);
        Assert.True(Check(result, "openInQuickBooks").Ok);
    }

    [Fact]
    public async Task Should_Fail_When_CompanyFileIsNotConfigured()
    {
        var validator = Build(new StubGateway(CompanyFile()));

        var result = await validator.ValidateAsync("   ", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Check(result, "configured").Ok);
        // Nothing further can be judged, so nothing further is claimed.
        Assert.DoesNotContain(result.Checks, c => c.Name == "exists");
    }

    [Theory]
    [InlineData("Tropicana.qbw")]
    [InlineData("TROPICANA.QBW")]
    public async Task Should_AcceptTheExtension_When_CaseDiffers(string name)
    {
        var file = CompanyFile(name);
        var validator = Build(new StubGateway(file));

        var result = await validator.ValidateAsync(file, CancellationToken.None);

        Assert.True(Check(result, "pathShape").Ok, Check(result, "pathShape").Message);
    }

    [Fact]
    public async Task Should_Fail_When_ThePathIsNotAQbwFile()
    {
        var file = _dir.Combine("Tropicana.txt");
        File.WriteAllText(file, "x");
        var validator = Build(new StubGateway(file));

        var result = await validator.ValidateAsync(file, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Check(result, "pathShape").Ok);
    }

    [Fact]
    public async Task Should_Fail_When_TheFileIsMissing()
    {
        var missing = _dir.Combine("Gone.QBW");
        var validator = Build(new StubGateway(missing));

        var result = await validator.ValidateAsync(missing, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(Check(result, "exists").Ok);
    }

    [Fact]
    public async Task Should_Fail_When_QuickBooksHasADifferentFileOpen()
    {
        // The dangerous one: posting would go into another company's books.
        var configured = CompanyFile();
        var other = CompanyFile("Other.QBW");
        var validator = Build(new StubGateway(other));

        var result = await validator.ValidateAsync(configured, CancellationToken.None);

        Assert.False(result.Ok);
        var open = Check(result, "openInQuickBooks");
        Assert.False(open.Ok);
        Assert.Equal(CompanyFileValidator.Error, open.Severity);
        Assert.Contains("Other.QBW", open.Message);
    }

    [Fact]
    public async Task Should_Pass_When_QuickBooksReportsTheSamePathDifferently()
    {
        // Same file, spelled in a different case: Windows paths are case-insensitive.
        var file = CompanyFile();
        var validator = Build(new StubGateway(file.ToUpperInvariant()));

        var result = await validator.ValidateAsync(file, CancellationToken.None);

        Assert.True(Check(result, "openInQuickBooks").Ok, Check(result, "openInQuickBooks").Message);
    }

    [Fact]
    public async Task Should_WarnOnly_When_TheBackupIsStale()
    {
        var file = CompanyFile();
        var backups = _dir.Combine("backups");
        Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, "Tropicana.QBB");
        File.WriteAllText(backup, "old backup");
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow.AddHours(-52));
        var validator = Build(new StubGateway(file), backups);

        var result = await validator.ValidateAsync(file, CancellationToken.None);

        var backupCheck = Check(result, "backupFreshness");
        Assert.False(backupCheck.Ok);
        Assert.Equal(CompanyFileValidator.Warning, backupCheck.Severity);
        // FR-11 still refuses to post on a stale backup; validating must not be what lets one through.
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Should_ReportUnavailable_When_QuickBooksCannotBeReached()
    {
        var file = CompanyFile();
        var validator = Build(new ThrowingGateway(new QuickBooksUnavailableException("QuickBooks is not running")));

        var result = await validator.ValidateAsync(file, CancellationToken.None);

        Assert.False(result.Ok);
        var open = Check(result, "openInQuickBooks");
        Assert.False(open.Ok);
        Assert.Contains("QuickBooks is not running", open.Message);
    }

    private sealed class StubGateway(string openCompanyFile) : IQbGateway
    {
        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => throw new InvalidOperationException("FR-A-5 sends no qbXML.");

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult(openCompanyFile);
    }

    private sealed class ThrowingGateway(Exception error) : IQbGateway
    {
        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => throw new InvalidOperationException("FR-A-5 sends no qbXML.");

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromException<string>(error);
    }
}
