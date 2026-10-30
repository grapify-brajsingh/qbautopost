using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Tests.Store;

/// <summary>T-1501 (M15 spec 3, 3.1, Q-125): the file mode's sync history, kept in memory since start and capped.</summary>
public sealed class FileQbListsHistoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qbautopost-tests", Guid.NewGuid().ToString("N"));

    public FileQbListsHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FileQbLists Store(int historyRows = 200) => new(Path.Combine(_dir, "qb-lists.json"), historyRows);

    private static QbLists Lists() => new()
    {
        SyncedUtc = T0,
        Accounts = [new QbAccount { Name = "Fuel", Type = "Expense" }],
        Vendors = ["Shell"],
        Customers = ["Acme"],
    };

    private static ListSyncRun Run(int minute, ListSyncOutcome outcome, ListSyncTrigger trigger = ListSyncTrigger.Schedule) => new()
    {
        Trigger = trigger,
        RequestedBy = trigger == ListSyncTrigger.Dashboard ? "user-1" : null,
        StartedUtc = T0.AddMinutes(minute),
        FinishedUtc = T0.AddMinutes(minute).AddSeconds(3),
        Outcome = outcome,
        Reason = outcome == ListSyncOutcome.Ok ? null : ListSyncReasons.QuickBooksNotOpen,
        Error = outcome == ListSyncOutcome.Failed ? "boom" : null,
        Accounts = outcome == ListSyncOutcome.Ok ? 1 : null,
        Vendors = outcome == ListSyncOutcome.Ok ? 1 : null,
        Customers = outcome == ListSyncOutcome.Ok ? 1 : null,
        MissingInRules = outcome == ListSyncOutcome.Ok ? ["Office"] : null,
    };

    private static ListSyncQuery All(int page = 1, int pageSize = 25) => new(null, null, page, pageSize);

    private static async Task RecordAsync(FileQbLists store, ListSyncRun run) =>
        await store.RecordAsync(run, run.Outcome == ListSyncOutcome.Ok ? Lists() : null, CancellationToken.None);

    [Fact]
    public async Task Should_ReturnEmptyPage_When_NothingRecordedSinceStart()
    {
        var page = await Store().ListAttemptsAsync(All(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.Null(page.CurrentId);
    }

    [Fact]
    public async Task Should_ReturnAttemptsNewestFirst_When_TwoSyncsRecorded()
    {
        var store = Store();
        await RecordAsync(store, Run(0, ListSyncOutcome.Ok));
        await RecordAsync(store, Run(30, ListSyncOutcome.Ok, ListSyncTrigger.Dashboard));

        var page = await store.ListAttemptsAsync(All(), CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.Equal(new long[] { 2, 1 }, page.Items.Select(i => i.Id));
        Assert.Equal(ListSyncTrigger.Dashboard, page.Items[0].Run.Trigger);
        Assert.Equal(2, page.CurrentId);
    }

    [Fact]
    public async Task Should_MarkNewestOkAsCurrent_When_LaterAttemptsFailed()
    {
        var store = Store();
        await RecordAsync(store, Run(0, ListSyncOutcome.Ok));
        await RecordAsync(store, Run(30, ListSyncOutcome.Failed));
        await RecordAsync(store, Run(60, ListSyncOutcome.Skipped));

        var page = await store.ListAttemptsAsync(All(), CancellationToken.None);

        Assert.Equal(1, page.CurrentId);
    }

    [Fact]
    public async Task Should_FilterByTriggerAndOutcome_When_SetsGiven()
    {
        var store = Store();
        await RecordAsync(store, Run(0, ListSyncOutcome.Ok));
        await RecordAsync(store, Run(30, ListSyncOutcome.Ok, ListSyncTrigger.Dashboard));
        await RecordAsync(store, Run(60, ListSyncOutcome.Skipped));

        var byTrigger = await store.ListAttemptsAsync(
            new ListSyncQuery(new HashSet<ListSyncTrigger> { ListSyncTrigger.Dashboard }, null, 1, 25), CancellationToken.None);
        var byOutcome = await store.ListAttemptsAsync(
            new ListSyncQuery(null, new HashSet<ListSyncOutcome> { ListSyncOutcome.Skipped }, 1, 25), CancellationToken.None);

        Assert.Equal(2, Assert.Single(byTrigger.Items).Id);
        Assert.Equal(1, byTrigger.Total);
        Assert.Equal(3, Assert.Single(byOutcome.Items).Id);
        Assert.Equal(2, byTrigger.CurrentId);
    }

    [Fact]
    public async Task Should_ReturnOnePageAndFullTotal_When_Paged()
    {
        var store = Store();
        for (var i = 0; i < 5; i++)
        {
            await RecordAsync(store, Run(i, ListSyncOutcome.Skipped));
        }

        var second = await store.ListAttemptsAsync(All(page: 2, pageSize: 2), CancellationToken.None);
        var past = await store.ListAttemptsAsync(All(page: 4, pageSize: 2), CancellationToken.None);

        Assert.Equal(new long[] { 3, 2 }, second.Items.Select(i => i.Id));
        Assert.Equal(5, second.Total);
        Assert.Empty(past.Items);
        Assert.Equal(5, past.Total);
    }

    [Fact]
    public async Task Should_KeepOnlyNewestRows_When_HistoryExceedsCap()
    {
        var store = Store(historyRows: 10);
        for (var i = 0; i < 15; i++)
        {
            await RecordAsync(store, Run(i, ListSyncOutcome.Skipped));
        }

        var page = await store.ListAttemptsAsync(All(pageSize: 100), CancellationToken.None);

        Assert.Equal(10, page.Total);
        Assert.Equal(15, page.Items[0].Id);
        Assert.Equal(6, page.Items[^1].Id);
    }

    [Fact]
    public async Task Should_DropErrorAndMissingNames_When_KeptInHistory()
    {
        var store = Store();
        await RecordAsync(store, Run(0, ListSyncOutcome.Ok));
        await RecordAsync(store, Run(30, ListSyncOutcome.Failed));

        var page = await store.ListAttemptsAsync(All(), CancellationToken.None);

        Assert.All(page.Items, i => Assert.Null(i.Run.Error));
        Assert.All(page.Items, i => Assert.Null(i.Run.MissingInRules));
        Assert.Equal((1, 1, 1), (page.Items[1].Run.Accounts, page.Items[1].Run.Vendors, page.Items[1].Run.Customers));
        Assert.Equal("boom", (await store.LastAttemptAsync(CancellationToken.None))!.Error);
    }

    [Fact]
    public void Should_Refuse_When_HistoryRowsIsNotPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Store(historyRows: 0));
    }
}
