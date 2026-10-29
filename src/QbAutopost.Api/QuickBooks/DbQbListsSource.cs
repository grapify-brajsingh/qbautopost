using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Data;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// Database mode (M14 §3, §9): the lists of the newest Ok row in PostgreSQL are <see cref="Current"/>, held in memory so
/// readers never touch the database. Every recorded attempt updates <see cref="LastAttempt"/>; only an Ok one replaces
/// <see cref="Current"/>. <c>qb-lists.json</c> is read once, for the import of §3.3, and never written.
/// </summary>
public sealed class DbQbListsSource(
    EfQbListsStore store,
    PipelineOptions options,
    IClock clock,
    ILogger<DbQbListsSource> log) : IQbListsStore, IQbListsSource
{
    private readonly object _gate = new();
    private State _state = new(QbLists.Empty, null);

    public QbLists Current => Volatile.Read(ref _state).Lists;

    public ListSyncRun? LastAttempt => Volatile.Read(ref _state).Last;

    /// <summary>
    /// Startup (M14 §3.3, §9): imports <c>qb-lists.json</c> if the database has no Ok row, then loads the current lists
    /// and the last attempt. A database error leaves empty lists, is logged once at Error and does not stop startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var fileLists = ReadFileLists();
            if (fileLists is not null && await store.ImportIfEmptyAsync(fileLists, clock.UtcNow, ct))
            {
                log.LogInformation(
                    "QuickBooks lists imported from {File} into the database ({Accounts} accounts, {Vendors} vendors, {Customers} customers)",
                    options.QbListsFile, fileLists.Accounts.Count, fileLists.Vendors.Count, fileLists.Customers.Count);
            }

            var current = await store.LoadCurrentAsync(ct) ?? QbLists.Empty;
            var last = await store.LastAttemptAsync(ct);
            Swap(_ => new State(current, last));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Swap(_ => new State(QbLists.Empty, null));
            log.LogError(ex, "QuickBooks lists could not be loaded from the database; starting with empty lists until a sync succeeds");
        }
    }

    public async Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
    {
        try
        {
            await store.RecordAsync(run, lists, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // QbListSync swallows a failure to record a Failed run; this is the only trace of it. No list contents.
            log.LogWarning(ex, "QuickBooks lists sync ({Trigger}, {Outcome}) could not be recorded in the database", run.Trigger, run.Outcome);
            throw;
        }

        // Current never moves backwards: a slower, older sync finishing late must not replace newer lists.
        Swap(s =>
        {
            var replace = run.Outcome == ListSyncOutcome.Ok && lists is not null
                && (s.Lists.SyncedUtc is null || lists.SyncedUtc is null || lists.SyncedUtc > s.Lists.SyncedUtc);
            return new State(replace ? lists! : s.Lists, run);
        });
    }

    public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Task.FromResult(LastAttempt);

    private QbLists? ReadFileLists()
    {
        try
        {
            return File.Exists(options.QbListsFile) ? new QbListsStore(options.QbListsFile).Load() : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "{File} could not be read, so it is not imported into the database", options.QbListsFile);
            return null;
        }
    }

    private void Swap(Func<State, State> next)
    {
        lock (_gate)
        {
            Volatile.Write(ref _state, next(_state));
        }
    }

    private sealed record State(QbLists Lists, ListSyncRun? Last);
}
