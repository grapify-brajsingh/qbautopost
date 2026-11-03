using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1403 (M14 spec 3): <see cref="EfQbListsStore"/> on a real PostgreSQL 16, migrated as the owner and used as the
/// <c>qbautopost_app</c> role with only the grants of deploy/grants.sql.
/// </summary>
public sealed class QbListSyncStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private static QbLists Lists(string tag, int accounts = 2) => new()
    {
        SyncedUtc = T0,
        Accounts = Enumerable.Range(1, accounts).Select(i => new QbAccount { Name = $"{tag} Account {i}", Type = i == 1 ? "Expense" : null }).ToList(),
        Vendors = [$"{tag} Vendor A", $"{tag} Vendor B", $"{tag} Vendor C"],
        Customers = [$"{tag} Customer"],
    };

    private static ListSyncRun OkRun(int minute, QbLists lists, ListSyncTrigger trigger = ListSyncTrigger.Schedule, string? by = null) => new()
    {
        Trigger = trigger,
        RequestedBy = by,
        StartedUtc = T0.AddMinutes(minute),
        FinishedUtc = T0.AddMinutes(minute).AddSeconds(4),
        Outcome = ListSyncOutcome.Ok,
        Accounts = lists.Accounts.Count,
        Vendors = lists.Vendors.Count,
        Customers = lists.Customers.Count,
        MissingInRules = ["Office Supplies", "Fuel"],
    };

    private static ListSyncRun FailedRun(int minute) => new()
    {
        Trigger = ListSyncTrigger.Dashboard,
        RequestedBy = "user-1",
        StartedUtc = T0.AddMinutes(minute),
        FinishedUtc = T0.AddMinutes(minute).AddSeconds(2),
        Outcome = ListSyncOutcome.Failed,
        Reason = ListSyncReasons.QuickBooksUnavailable,
        Error = "QuickBooks is not responding",
    };

    private static ListSyncRun SkippedRun(int minute) => new()
    {
        Trigger = ListSyncTrigger.Schedule,
        StartedUtc = T0.AddMinutes(minute),
        FinishedUtc = T0.AddMinutes(minute),
        Outcome = ListSyncOutcome.Skipped,
        Reason = ListSyncReasons.QuickBooksNotOpen,
    };

    [PostgresFact]
    public async Task Should_RoundTripEveryField_When_OkRunRecorded()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        var lists = Lists("A");
        var run = OkRun(0, lists, ListSyncTrigger.Api, "ops-script");

        await store.RecordAsync(run, lists, CancellationToken.None);

        var last = await store.LastAttemptAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(run, last, new RunComparer());
        Assert.Equal(lists, await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
    }

    [PostgresFact]
    public async Task Should_KeepPreviousLists_When_FailedOrSkippedRunRecorded()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        var lists = Lists("A");
        await store.RecordAsync(OkRun(0, lists), lists, CancellationToken.None);

        await store.RecordAsync(FailedRun(30), null, CancellationToken.None);
        await store.RecordAsync(SkippedRun(60), Lists("ignored"), CancellationToken.None);

        Assert.Equal(lists, await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
        Assert.Equal(2, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs WHERE outcome <> 'ok' AND lists IS NULL AND missing_in_rules IS NULL"));
    }

    [PostgresFact]
    public async Task Should_ReturnNewestRowOfAnyOutcome_When_ReadingLastAttempt()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        Assert.Null(await store.LastAttemptAsync(CancellationToken.None));
        Assert.Null(await store.LoadCurrentAsync(CancellationToken.None));
        var lists = Lists("A");
        await store.RecordAsync(OkRun(0, lists), lists, CancellationToken.None);

        await store.RecordAsync(FailedRun(30), null, CancellationToken.None);
        Assert.Equal(FailedRun(30), await store.LastAttemptAsync(CancellationToken.None), new RunComparer());

        await store.RecordAsync(SkippedRun(60), null, CancellationToken.None);
        Assert.Equal(SkippedRun(60), await store.LastAttemptAsync(CancellationToken.None), new RunComparer());
    }

    [PostgresFact]
    public async Task Should_ReturnNewestOkLists_When_SeveralOkRuns()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        await store.RecordAsync(OkRun(0, Lists("old")), Lists("old"), CancellationToken.None);
        await store.RecordAsync(OkRun(30, Lists("new")), Lists("new"), CancellationToken.None);
        await store.RecordAsync(FailedRun(60), null, CancellationToken.None);

        Assert.Equal(Lists("new"), await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
    }

    [PostgresFact]
    public async Task Should_KeepOnlyNewestRows_When_HistoryExceedsLimit()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store(historyRows: 10);
        for (var i = 0; i < 15; i++)
        {
            await store.RecordAsync(SkippedRun(i), null, CancellationToken.None);
        }

        Assert.Equal(10, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs"));
        Assert.Equal(6, await db.ScalarAsync("SELECT min(id) FROM qb_list_syncs"));
    }

    [PostgresFact]
    public async Task Should_KeepNewestOkRow_When_ItIsOlderThanTheLimit()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store(historyRows: 10);
        var lists = Lists("A");
        await store.RecordAsync(OkRun(0, lists), lists, CancellationToken.None);
        for (var i = 1; i <= 14; i++)
        {
            await store.RecordAsync(FailedRun(i * 30), null, CancellationToken.None);
        }

        Assert.Equal(11, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs"));
        Assert.Equal(lists, await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());

        // A newer ok row becomes the one never deleted, so the old one goes with that insert.
        var newer = Lists("B");
        await store.RecordAsync(OkRun(500, newer), newer, CancellationToken.None);
        Assert.Equal(10, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs"));
        Assert.Equal(newer, await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
    }

    [PostgresFact]
    public async Task Should_InsertImportRowOnce_When_NoOkRowExists()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        var lists = Lists("file", accounts: 3);

        Assert.True(await store.ImportIfEmptyAsync(lists, T0, CancellationToken.None));
        Assert.False(await store.ImportIfEmptyAsync(Lists("other"), T0.AddMinutes(1), CancellationToken.None));

        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs"));
        var last = await store.LastAttemptAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(ListSyncTrigger.Import, last.Trigger);
        Assert.Equal(ListSyncOutcome.Ok, last.Outcome);
        Assert.Equal((3, 3, 1), (last.Accounts, last.Vendors, last.Customers));
        Assert.Null(last.MissingInRules);
        Assert.Equal(T0, last.StartedUtc);
        Assert.Equal(T0, last.FinishedUtc);
        Assert.Equal(lists, await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
    }

    [PostgresFact]
    public async Task Should_ImportNothing_When_OkRowAlreadyExists()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        var lists = Lists("A");
        await store.RecordAsync(OkRun(0, lists), lists, CancellationToken.None);

        Assert.False(await store.ImportIfEmptyAsync(Lists("file"), T0.AddMinutes(5), CancellationToken.None));

        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs"));
        Assert.Equal(lists, await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
    }

    [PostgresFact]
    public async Task Should_ImportFileLists_When_OnlyFailedRowsExist()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        await store.RecordAsync(FailedRun(0), null, CancellationToken.None);

        Assert.True(await store.ImportIfEmptyAsync(Lists("file"), T0.AddMinutes(5), CancellationToken.None));

        Assert.Equal(Lists("file"), await store.LoadCurrentAsync(CancellationToken.None), new ListsComparer());
    }

    [PostgresFact]
    public async Task Should_CreateTableColumnsAndIndex_When_Migrated()
    {
        await using var db = await Db.CreateAsync(postgres);

        var columns = await db.StringsAsync(
            "SELECT column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns WHERE table_name = 'qb_list_syncs' ORDER BY 1");
        Assert.Equal(
            new[]
            {
                "accounts:integer:YES",
                "customers:integer:YES",
                "error:text:YES",
                "finished_utc:timestamp with time zone:NO",
                "id:bigint:NO",
                "lists:jsonb:YES",
                "missing_in_rules:jsonb:YES",
                "outcome:text:NO",
                "reason:text:YES",
                "requested_by:text:YES",
                "started_utc:timestamp with time zone:NO",
                "trigger:text:NO",
                "vendors:integer:YES",
            },
            columns);

        var indexes = await db.StringsAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_qb_list_syncs_outcome_id'");
        Assert.Contains("(outcome, id DESC)", Assert.Single(indexes));
    }

    [PostgresFact]
    public async Task Should_AllowInsertSelectDeleteButNotUpdate_When_RunningAsAppRole()
    {
        await using var db = await Db.CreateAsync(postgres);

        foreach (var (privilege, expected) in new[] { ("SELECT", true), ("INSERT", true), ("DELETE", true), ("UPDATE", false), ("TRUNCATE", false) })
        {
            var held = await db.StringsAsync($"SELECT has_table_privilege('{PostgresFixture.AppRole}', 'qb_list_syncs', '{privilege}')::text");
            Assert.Equal(new[] { expected ? "true" : "false" }, held);
        }
    }

    [Fact]
    public void Should_NameTableWithItsThreeRights_When_ReadingGrants()
    {
        var grants = string.Join(
            '\n',
            File.ReadAllLines(PostgresFixture.RepoFile("deploy", "grants.sql")).Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.Matches(@"GRANT SELECT, INSERT, DELETE ON qb_list_syncs TO qbautopost_app;", grants);
    }

    // ---- M15 §3.1 (T-1501): the history ------------------------------------------------------------------------

    private static ListSyncQuery All(int page = 1, int pageSize = 25) => new(null, null, page, pageSize);

    [PostgresFact]
    public async Task Should_ReturnEmptyPage_When_NoAttemptRecorded()
    {
        await using var db = await Db.CreateAsync(postgres);

        var page = await db.Store().ListAttemptsAsync(All(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Null(page.CurrentId);
    }

    [PostgresFact]
    public async Task Should_RefuseTheRun_When_AnOkRunCarriesNoLists()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();

        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordAsync(OkRun(0, Lists("A")), null, CancellationToken.None));

        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM qb_list_syncs"));
    }

    [PostgresFact]
    public async Task Should_ReturnAttemptsNewestFirstWithCurrentOk_When_Listing()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        await store.RecordAsync(OkRun(0, Lists("A")), Lists("A"), CancellationToken.None);
        await store.RecordAsync(OkRun(30, Lists("B"), ListSyncTrigger.Dashboard, "user-9"), Lists("B"), CancellationToken.None);
        await store.RecordAsync(SkippedRun(60), null, CancellationToken.None);

        var page = await store.ListAttemptsAsync(All(), CancellationToken.None);

        Assert.Equal(3, page.Total);
        Assert.Equal(new long[] { 3, 2, 1 }, page.Items.Select(i => i.Id));
        Assert.Equal(2, page.CurrentId);
        var dashboard = page.Items[1].Run;
        Assert.Equal((ListSyncTrigger.Dashboard, "user-9", ListSyncOutcome.Ok), (dashboard.Trigger, dashboard.RequestedBy, dashboard.Outcome));
        Assert.Equal((2, 3, 1), (dashboard.Accounts, dashboard.Vendors, dashboard.Customers));
        Assert.Equal(T0.AddMinutes(30), dashboard.StartedUtc);
        Assert.Equal(T0.AddMinutes(30).AddSeconds(4), dashboard.FinishedUtc);
        Assert.Equal(ListSyncReasons.QuickBooksNotOpen, page.Items[0].Run.Reason);
    }

    [PostgresFact]
    public async Task Should_NeverCarryErrorOrMissingNames_When_Listing()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        await store.RecordAsync(OkRun(0, Lists("A")), Lists("A"), CancellationToken.None);
        await store.RecordAsync(FailedRun(30), null, CancellationToken.None);

        var page = await store.ListAttemptsAsync(All(), CancellationToken.None);

        Assert.All(page.Items, i => Assert.Null(i.Run.Error));
        Assert.All(page.Items, i => Assert.Null(i.Run.MissingInRules));
    }

    [PostgresFact]
    public async Task Should_FilterByTriggerAndOutcomeAndCountFilteredSet_When_SetsGiven()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        await store.RecordAsync(OkRun(0, Lists("A"), ListSyncTrigger.Import), Lists("A"), CancellationToken.None);
        await store.RecordAsync(OkRun(10, Lists("A")), Lists("A"), CancellationToken.None);
        await store.RecordAsync(FailedRun(20), null, CancellationToken.None);
        await store.RecordAsync(SkippedRun(30), null, CancellationToken.None);

        var triggers = await store.ListAttemptsAsync(
            new ListSyncQuery(new HashSet<ListSyncTrigger> { ListSyncTrigger.Schedule, ListSyncTrigger.Import }, null, 1, 25), CancellationToken.None);
        var outcomes = await store.ListAttemptsAsync(
            new ListSyncQuery(null, new HashSet<ListSyncOutcome> { ListSyncOutcome.Failed, ListSyncOutcome.Skipped }, 1, 25), CancellationToken.None);
        var both = await store.ListAttemptsAsync(
            new ListSyncQuery(new HashSet<ListSyncTrigger> { ListSyncTrigger.Schedule }, new HashSet<ListSyncOutcome> { ListSyncOutcome.Skipped }, 1, 25),
            CancellationToken.None);

        Assert.Equal(new long[] { 4, 2, 1 }, triggers.Items.Select(i => i.Id));
        Assert.Equal(3, triggers.Total);
        Assert.Equal(new long[] { 4, 3 }, outcomes.Items.Select(i => i.Id));
        Assert.Equal(2, outcomes.Total);
        Assert.Equal(4, Assert.Single(both.Items).Id);
        Assert.Equal(2, outcomes.CurrentId);
    }

    [PostgresFact]
    public async Task Should_ReturnOnePageAndFullTotal_When_Paged()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(SkippedRun(i), null, CancellationToken.None);
        }

        var second = await store.ListAttemptsAsync(All(page: 2, pageSize: 2), CancellationToken.None);
        var past = await store.ListAttemptsAsync(All(page: 9, pageSize: 2), CancellationToken.None);

        Assert.Equal(new long[] { 3, 2 }, second.Items.Select(i => i.Id));
        Assert.Equal(5, second.Total);
        Assert.Empty(past.Items);
        Assert.Equal(5, past.Total);
        Assert.Null(second.CurrentId);
    }

    [PostgresFact]
    public async Task Should_ReadNeitherListsNorErrorNorMissingNames_When_Listing()
    {
        await using var db = await Db.CreateAsync(postgres);
        var store = db.Store();
        await store.RecordAsync(OkRun(0, Lists("A")), Lists("A"), CancellationToken.None);
        await store.RecordAsync(FailedRun(30), null, CancellationToken.None);

        // The app role may now read every column but these three: a query that reads one of them is refused.
        await db.StringsAsync($"REVOKE SELECT ON qb_list_syncs FROM {PostgresFixture.AppRole}");
        await db.StringsAsync(
            "GRANT SELECT (id, trigger, requested_by, started_utc, finished_utc, outcome, reason, accounts, vendors, customers) "
            + $"ON qb_list_syncs TO {PostgresFixture.AppRole}");

        var page = await store.ListAttemptsAsync(All(), CancellationToken.None);
        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.CurrentId);
        await Assert.ThrowsAnyAsync<Exception>(() => store.LastAttemptAsync(CancellationToken.None));
    }

    private sealed class RunComparer : IEqualityComparer<ListSyncRun?>
    {
        public bool Equals(ListSyncRun? x, ListSyncRun? y) =>
            x is not null && y is not null
            && (x.Trigger, x.RequestedBy, x.StartedUtc, x.FinishedUtc, x.Outcome, x.Reason, x.Error, x.Accounts, x.Vendors, x.Customers)
               == (y.Trigger, y.RequestedBy, y.StartedUtc, y.FinishedUtc, y.Outcome, y.Reason, y.Error, y.Accounts, y.Vendors, y.Customers)
            && (x.MissingInRules is null ? y.MissingInRules is null : y.MissingInRules is not null && x.MissingInRules.SequenceEqual(y.MissingInRules));

        public int GetHashCode(ListSyncRun? obj) => 0;
    }

    private sealed class ListsComparer : IEqualityComparer<QbLists?>
    {
        public bool Equals(QbLists? x, QbLists? y) =>
            x is not null && y is not null
            && x.SyncedUtc == y.SyncedUtc
            && x.Accounts.Select(a => (a.Name, a.Type)).SequenceEqual(y.Accounts.Select(a => (a.Name, a.Type)))
            && x.Vendors.SequenceEqual(y.Vendors)
            && x.Customers.SequenceEqual(y.Customers);

        public int GetHashCode(QbLists? obj) => 0;
    }

    private sealed class Db : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _app;
        private readonly string _admin;

        private Db(ServiceProvider services, string app, string admin)
        {
            _services = services;
            _app = app;
            _admin = admin;
        }

        public static async Task<Db> CreateAsync(PostgresFixture postgres)
        {
            var (admin, app) = await postgres.CreateAppRoleDatabaseAsync();
            var services = new ServiceCollection().AddQbAutopostData(app, commandTimeoutSeconds: 30).BuildServiceProvider();
            return new Db(services, app, admin);
        }

        public EfQbListsStore Store(int historyRows = EfQbListsStore.DefaultHistoryRows) =>
            new(_services.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>(), historyRows);

        public async Task<long> ScalarAsync(string sql) => long.Parse((await StringsAsync(sql)).Single(), CultureInfo.InvariantCulture);

        public async Task<List<string>> StringsAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync())
            {
                rows.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
            }

            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            Api.TestDatabase.Release(_app);
        }
    }
}
