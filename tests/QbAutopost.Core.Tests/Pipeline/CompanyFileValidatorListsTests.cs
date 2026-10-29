using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>M14 §9 (T-1404): <c>listsSynced</c> asks the lists source, not the file, so it is right in database mode.</summary>
public sealed class CompanyFileValidatorListsTests : IDisposable
{
    private readonly TempJobFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<CompanyFileCheck> ListsSyncedAsync(IQbListsSource lists)
    {
        var company = Path.Combine(_dir.Root, "Tropicana.QBW");
        File.WriteAllText(company, "not a real company file");
        var options = new PipelineOptions
        {
            CompanyName = "Tropicana Properties LLC",
            RulesFile = Path.Combine(_dir.Root, "rules.json"),
            LedgerFile = Path.Combine(_dir.Root, "ledger.json"),
            // The file does not exist: only the source may decide.
            QbListsFile = Path.Combine(_dir.Root, "missing", "qb-lists.json"),
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
