using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// M14 T-1404 (spec §3.3, §3.5, §9): the real <c>Program</c> in database mode. A sync is recorded and replaces the
/// current lists, <c>qb-lists.json</c> is imported once and never written, and a second start imports nothing.
/// </summary>
public sealed class QbListsSourceDbWiringTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly QbLists FileLists = new()
    {
        SyncedUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        Accounts = [new QbAccount { Name = "Imported Account", Type = "Expense" }],
        Vendors = ["Imported Vendor"],
        Customers = ["Imported Customer"],
    };

    private static async Task<List<(ListSyncTrigger Trigger, ListSyncOutcome Outcome)>> RowsAsync(ApiFactory factory)
    {
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.QbListSyncs.AsNoTracking().OrderBy(r => r.Id).Select(r => new { r.Trigger, r.Outcome }).ToListAsync();
        return rows.Select(r => (r.Trigger, r.Outcome)).ToList();
    }

    private static void SeedFile(ApiFactory factory) =>
        new QbListsStore(factory.Dir.Combine("data", "qb-lists.json")).Save(FileLists);

    [PostgresFact]
    public async Task Should_RecordRowAndUpdateCurrentAndNotWriteFile_When_SyncRunsInDatabaseMode()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        try
        {
            using var factory = ApiFactory.WithPostgres(connectionString);
            factory.Gateway.Company.Accounts.Add(new QbAccount { Name = "Utilities", Type = "Expense" });
            using var client = factory.CreateAuthorizedClient();

            var source = factory.Services.GetRequiredService<IQbListsSource>();
            Assert.IsType<DbQbListsSource>(source);
            Assert.Empty(source.Current.Accounts);

            using var response = await client.PostAsync("/qb/sync-lists", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(source.Current.Accounts, a => a.Name == "Utilities");
            Assert.Equal(ListSyncOutcome.Ok, source.LastAttempt!.Outcome);
            Assert.Equal([(ListSyncTrigger.Api, ListSyncOutcome.Ok)], await RowsAsync(factory));
            Assert.False(File.Exists(factory.Dir.Combine("data", "qb-lists.json")));
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_ImportFileOnceAndLoadItAsCurrent_When_DatabaseIsEmptyAndFileExists()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        try
        {
            using var factory = ApiFactory.WithPostgres(connectionString);
            SeedFile(factory);
            using var client = factory.CreateAuthorizedClient(); // starts the host

            var source = factory.Services.GetRequiredService<IQbListsSource>();

            Assert.Equal(FileLists.SyncedUtc, source.Current.SyncedUtc);
            Assert.Equal(["Imported Account"], source.Current.Accounts.Select(a => a.Name));
            Assert.Equal(["Imported Vendor"], source.Current.Vendors);
            Assert.Equal(["Imported Customer"], source.Current.Customers);
            Assert.Equal([(ListSyncTrigger.Import, ListSyncOutcome.Ok)], await RowsAsync(factory));
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_KeepOneImportRow_When_HostStartsTwiceWithTheFile()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        try
        {
            using (var first = ApiFactory.WithPostgres(connectionString))
            {
                SeedFile(first);
                using var client = first.CreateAuthorizedClient();
                Assert.Single(await RowsAsync(first));
            }

            using var second = ApiFactory.WithPostgres(connectionString);
            SeedFile(second);
            using var secondClient = second.CreateAuthorizedClient();

            Assert.Equal([(ListSyncTrigger.Import, ListSyncOutcome.Ok)], await RowsAsync(second));
            Assert.Equal(["Imported Account"], second.Services.GetRequiredService<IQbListsSource>().Current.Accounts.Select(a => a.Name));
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    private static ListSyncRun Run(ListSyncOutcome outcome, int minute) => new()
    {
        Trigger = ListSyncTrigger.Schedule,
        StartedUtc = new DateTime(2026, 10, 29, 10, minute, 0, DateTimeKind.Utc),
        FinishedUtc = new DateTime(2026, 10, 29, 10, minute, 1, DateTimeKind.Utc),
        Outcome = outcome,
        Reason = outcome == ListSyncOutcome.Ok ? null : ListSyncReasons.QuickBooksNotOpen,
    };

    private static QbLists Synced(string name, int minute) => new()
    {
        SyncedUtc = new DateTime(2026, 10, 29, 10, minute, 0, DateTimeKind.Utc),
        Accounts = [new QbAccount { Name = name }],
    };

    private static DbQbListsSource NewSource(string connectionString, ListLogger<DbQbListsSource> log, string listsFile)
    {
        var services = new ServiceCollection().AddQbAutopostData(connectionString, 30).BuildServiceProvider();
        return new DbQbListsSource(
            services.GetRequiredService<EfQbListsStore>(),
            new PipelineOptions { CompanyName = "c", RulesFile = "r", LedgerFile = "l", QbListsFile = listsFile },
            new FixedClock(),
            log);
    }

    [PostgresFact]
    public async Task Should_KeepCurrentAndUpdateLastAttempt_When_FailedOrSkippedRunIsRecorded()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        try
        {
            var source = NewSource(connectionString, new ListLogger<DbQbListsSource>(), "missing.json");
            await source.RecordAsync(Run(ListSyncOutcome.Ok, 0), Synced("A", 0), default);

            await source.RecordAsync(Run(ListSyncOutcome.Failed, 5), null, default);
            Assert.Equal(["A"], source.Current.Accounts.Select(a => a.Name));
            Assert.Equal(ListSyncOutcome.Failed, source.LastAttempt!.Outcome);

            await source.RecordAsync(Run(ListSyncOutcome.Skipped, 10), null, default);
            Assert.Equal(["A"], source.Current.Accounts.Select(a => a.Name));
            Assert.Equal(ListSyncOutcome.Skipped, source.LastAttempt!.Outcome);
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_ReplaceCurrent_When_AnOkRunIsRecordedAfterTheClockSteppedBack()
    {
        // Syncs are serialised on the worker, so the last Ok recorded is the newest; its SyncedUtc can still be
        // earlier than the previous one's after a clock correction, and must not freeze the lists.
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        try
        {
            var source = NewSource(connectionString, new ListLogger<DbQbListsSource>(), "missing.json");
            await source.RecordAsync(Run(ListSyncOutcome.Ok, 20), Synced("Before", 20), default);

            await source.RecordAsync(Run(ListSyncOutcome.Ok, 10), Synced("After", 10), default);

            Assert.Equal(["After"], source.Current.Accounts.Select(a => a.Name));
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    [Fact]
    public async Task Should_LogWarningAndRethrow_When_DatabaseWriteFails()
    {
        var log = new ListLogger<DbQbListsSource>();
        var source = NewSource("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2;Pooling=false", log, "missing.json");

        await Assert.ThrowsAnyAsync<Exception>(() => source.RecordAsync(Run(ListSyncOutcome.Ok, 0), Synced("A", 0), default));

        Assert.Single(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Null(source.LastAttempt);
        Assert.Null(source.Current.SyncedUtc);
    }

    [Fact]
    public async Task Should_StartWithEmptyListsAndLogError_When_ImportFileCannotBeRead()
    {
        var log = new ListLogger<DbQbListsSource>();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // A directory where the file should be: File.Exists is false, so use an unreadable (invalid) file instead.
            var bad = Path.Combine(dir.FullName, "qb-lists.json");
            File.WriteAllText(bad, "{ not json");
            var source = NewSource("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=2;Command Timeout=2;Pooling=false", log, bad);

            await source.InitializeAsync(default);

            Assert.Null(source.Current.SyncedUtc);
            Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error || e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
