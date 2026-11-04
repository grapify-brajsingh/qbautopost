using System.Collections.Concurrent;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Data;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// Database mode (M14 §3, §9): the lists of the newest Ok row in PostgreSQL are <see cref="Current"/>, held in memory so
/// readers never touch the database. Every recorded attempt updates <see cref="LastAttempt"/>; only an Ok one replaces
/// <see cref="Current"/>. <c>qb-lists.json</c> is read once, for the import of §3.3, and never written.
/// <para>
/// M17 §4.7: this is the <see cref="IQbListsCatalog"/> too. Each company has its own lists in memory and its own rows
/// (<see cref="EfQbListsStore.ForCompany"/>); the plain <see cref="IQbListsStore"/>/<see cref="IQbListsSource"/>
/// members are the default company's view, so readiness, v1 <c>/quickbooks/lists</c>, the rules editor and the company
/// file check are unchanged. Without a resolver (tests) the default company is the rows with no company.
/// </para>
/// </summary>
public sealed class DbQbListsSource(
    EfQbListsStore store,
    PipelineOptions options,
    IClock clock,
    ILogger<DbQbListsSource> log,
    ICompanyResolver? resolver = null,
    ICompanyStore? companies = null) : IQbListsStore, IQbListsSource, IQbListsCatalog
{
    private readonly ConcurrentDictionary<int, CompanyLists> _lists = new();
    private volatile bool _loaded;

    /// <summary>The default company's id when the lists were last loaded; a later id (bootstrap succeeded since) reloads.</summary>
    private int _loadedDefaultId;

    public QbLists Current => Default.Current;

    public ListSyncRun? LastAttempt => Default.LastAttempt;

    /// <summary>The default company's id (0 until start-up has created it, or without a resolver).</summary>
    private int DefaultId => resolver?.Default.Id ?? 0;

    private CompanyLists Default => ListsOf(DefaultId);

    public IQbListsSource SourceFor(int companyId) => ListsOf(companyId);

    public IQbListsStore StoreFor(int companyId) => ListsOf(companyId);

    /// <summary>
    /// Startup (M14 §3.3, §9; M17 §4.4): imports <c>qb-lists.json</c> into the <b>default</b> company if it has no Ok row,
    /// then loads the current lists and the last attempt of every company. A database error leaves empty lists, is logged
    /// at Error and does not stop startup; <see cref="EnsureLoadedAsync"/> tries again.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        var defaultId = DefaultId;
        try
        {
            var fileLists = ReadFileLists();
            if (fileLists is not null && await store.ForCompany(defaultId).ImportIfEmptyAsync(fileLists, clock.UtcNow, ct))
            {
                log.LogInformation(
                    "QuickBooks lists imported from {File} into the database ({Accounts} accounts, {Vendors} vendors, {Customers} customers)",
                    options.QbListsFile, fileLists.Accounts.Count, fileLists.Vendors.Count, fileLists.Customers.Count);
            }

            var ids = new SortedSet<int> { defaultId };
            if (companies is not null)
            {
                ids.UnionWith((await companies.ListAsync(ct)).Select(c => c.Id));
            }

            foreach (var id in ids)
            {
                await ListsOf(id).LoadAsync(ct);
            }

            Volatile.Write(ref _loadedDefaultId, defaultId);
            _loaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "QuickBooks lists could not be loaded from the database; the lists stay empty until the database is back or a sync succeeds");
        }
    }

    /// <summary>
    /// Loads the lists if the startup load failed, or if it ran before the default company had its id (bootstrap
    /// failed then and has succeeded since: the default view moved to a company not loaded yet, whose rows bootstrap has
    /// just backfilled). Called by <c>DatabaseGate</c>'s recovery, after bootstrap, when the database first answers,
    /// which is before any saved-request route can post against empty lists. No timer (CLAUDE.md).
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken ct) =>
        _loaded && Volatile.Read(ref _loadedDefaultId) == DefaultId ? Task.CompletedTask : InitializeAsync(ct);

    public Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct) => Default.RecordAsync(run, lists, ct);

    public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Default.LastAttemptAsync(ct);

    /// <summary>M15 §3.1: the history is read from the database on each call; it is not held in memory.</summary>
    public Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct) => Default.ListAttemptsAsync(query, ct);

    private CompanyLists ListsOf(int companyId) =>
        _lists.GetOrAdd(companyId, id => new CompanyLists(store.ForCompany(id), log));

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

    /// <summary>One company's lists: held in memory, recorded through a store bound to that company. An unknown id reads empty.</summary>
    private sealed class CompanyLists(EfQbListsStore store, ILogger log) : IQbListsStore, IQbListsSource
    {
        private readonly object _gate = new();
        private State _state = new(QbLists.Empty, null, false);

        public QbLists Current => Volatile.Read(ref _state).Lists;

        public ListSyncRun? LastAttempt => Volatile.Read(ref _state).Last;

        public async Task LoadAsync(CancellationToken ct)
        {
            var current = await store.LoadCurrentAsync(ct) ?? QbLists.Empty;
            var last = await store.LastAttemptAsync(ct);
            // A sync recorded meanwhile is newer than this read: it keeps its lists.
            Swap(s => s.Loaded ? s : new State(current, last, true));
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
                log.LogWarning(
                    ex, "QuickBooks lists sync ({Trigger}, {Outcome}) of company {CompanyId} could not be recorded in the database",
                    run.Trigger, run.Outcome, store.CompanyId);
                throw;
            }

            // Syncs are serialised on the single worker, so the Ok run recorded last is the newest one: it always replaces
            // Current. No SyncedUtc comparison: after a backward clock step it would freeze the lists until the clock caught up.
            Swap(s =>
            {
                var replace = run.Outcome == ListSyncOutcome.Ok && lists is not null;
                return new State(replace ? lists! : s.Lists, run, s.Loaded || replace);
            });
        }

        public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Task.FromResult(LastAttempt);

        public Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct) => store.ListAttemptsAsync(query, ct);

        private void Swap(Func<State, State> next)
        {
            lock (_gate)
            {
                Volatile.Write(ref _state, next(_state));
            }
        }

        private sealed record State(QbLists Lists, ListSyncRun? Last, bool Loaded);
    }
}
