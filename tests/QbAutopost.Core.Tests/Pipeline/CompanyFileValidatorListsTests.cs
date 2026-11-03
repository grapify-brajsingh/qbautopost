using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>M14 §9 (T-1404): <c>listsSynced</c> asks the lists source, not the file, so it is right in database mode.</summary>
public sealed class CompanyFileValidatorListsTests : IDisposable
{
    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<CompanyFileCheck> ListsSyncedAsync(IQbListsSource lists, string? listsFile = null)
    {
        var company = Path.Combine(_dir.Root, "Tropicana.QBW");
        File.WriteAllText(company, "not a real company file");
        var options = new PipelineOptions
        {
            CompanyName = "Tropicana Properties LLC",
            RulesFile = Path.Combine(_dir.Root, "rules.json"),
            LedgerFile = Path.Combine(_dir.Root, "ledger.json"),
            // The file does not exist: only the source may decide.
            QbListsFile = listsFile ?? Path.Combine(_dir.Root, "missing", "qb-lists.json"),
        };

        var result = await new CompanyFileValidator(options, new OpenGateway(company), lists).ValidateAsync(company, default);

        return Assert.Single(result.Checks, c => c.Name == "listsSynced");
    }

    [Fact]
    public async Task Should_PassListsSynced_When_SourceHasSyncedLists()
    {
        var check = await ListsSyncedAsync(new FixedLists(new QbLists { SyncedUtc = new DateTime(2026, 10, 29, 9, 30, 0, DateTimeKind.Utc) }));

        Assert.True(check.Ok);
        Assert.Equal(CompanyFileValidator.Warning, check.Severity);
    }

    [Fact]
    public async Task Should_WarnListsSynced_When_SourceIsEmpty()
    {
        var check = await ListsSyncedAsync(new FixedLists(QbLists.Empty));

        Assert.False(check.Ok);
        Assert.Equal(CompanyFileValidator.Warning, check.Severity);
        Assert.Contains("POST /api/v1/quickbooks/lists/sync", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_UseStableTextInDatabaseMode_When_SourceHasSyncedLists()
    {
        var check = await ListsSyncedAsync(new FixedLists(new QbLists { SyncedUtc = new DateTime(2026, 10, 29, 9, 30, 0, DateTimeKind.Utc) }));

        Assert.Equal("lists synced 2026-10-29T09:30:00Z", check.Message);
    }

    [Fact]
    public async Task Should_UseStableTextInDatabaseMode_When_SourceIsEmpty()
    {
        var check = await ListsSyncedAsync(new FixedLists(QbLists.Empty));

        Assert.Equal("no lists synced yet; run POST /api/v1/quickbooks/lists/sync", check.Message);
    }

    [Fact]
    public async Task Should_KeepPreM14Text_When_FileModeAndFileExists()
    {
        var file = Path.Combine(_dir.Root, "data", "qb-lists.json");
        var lists = new FileQbLists(file);
        await lists.RecordAsync(
            new ListSyncRun { Trigger = ListSyncTrigger.Api, StartedUtc = DateTime.UtcNow, FinishedUtc = DateTime.UtcNow, Outcome = ListSyncOutcome.Ok },
            new QbLists { SyncedUtc = DateTime.UtcNow },
            default);

        var check = await ListsSyncedAsync(lists, file);

        Assert.True(check.Ok);
        Assert.Equal(file, check.Message);
    }

    [Fact]
    public async Task Should_KeepPreM14Text_When_FileModeAndFileMissing()
    {
        var file = Path.Combine(_dir.Root, "data", "qb-lists.json");

        var check = await ListsSyncedAsync(new FileQbLists(file), file);

        Assert.False(check.Ok);
        Assert.Equal($"{file} not found; run POST /api/v1/quickbooks/lists/sync", check.Message);
    }

    [Fact]
    public async Task Should_AskTheSource_When_ADecoratorWrapsTheFileLists()
    {
        var file = Path.Combine(_dir.Root, "data", "qb-lists.json");

        var check = await ListsSyncedAsync(new Wrapped(new FileQbLists(file)), file);

        Assert.Equal($"{file} not found; run POST /api/v1/quickbooks/lists/sync", check.Message);
    }

    private sealed class Wrapped(IQbListsSource inner) : IQbListsSource
    {
        public QbLists Current => inner.Current;

        public ListSyncRun? LastAttempt => inner.LastAttempt;

        public QbListsStatus Status() => inner.Status();
    }

    private sealed class FixedLists(QbLists current) : IQbListsSource
    {
        public QbLists Current => current;

        public ListSyncRun? LastAttempt => null;
    }

    private sealed class OpenGateway(string company) : IQbGateway
    {
        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult(company);
    }
}
