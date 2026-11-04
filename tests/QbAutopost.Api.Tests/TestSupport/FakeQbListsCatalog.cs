using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Ruling B3: the lists of every company for a host without PostgreSQL. The default company keeps the host's own
/// <c>qb-lists.json</c> store (<paramref name="defaultLists"/>, so v1 and the default dashboard behave as in production
/// file mode); every other company gets its own in-memory current lists and history.
/// </summary>
public sealed class FakeQbListsCatalog(IQbListsStore defaultLists, ICompanyResolver resolver) : IQbListsCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<int, InMemoryLists> _companies = [];

    public IQbListsSource SourceFor(int companyId) =>
        companyId == resolver.Default.Id ? (IQbListsSource)defaultLists : Lists(companyId);

    public IQbListsStore StoreFor(int companyId) =>
        companyId == resolver.Default.Id ? defaultLists : Lists(companyId);

    private InMemoryLists Lists(int companyId)
    {
        lock (_gate)
        {
            if (!_companies.TryGetValue(companyId, out var lists))
            {
                lists = new InMemoryLists();
                _companies[companyId] = lists;
            }

            return lists;
        }
    }

    /// <summary>One company: only an Ok run replaces the current lists; every run joins the history, newest first.</summary>
    private sealed class InMemoryLists : IQbListsStore, IQbListsSource
    {
        private readonly object _gate = new();
        private readonly List<ListSyncAttempt> _history = [];
        private QbLists _current = QbLists.Empty;
        private ListSyncRun? _last;
        private long? _currentId;

        public QbLists Current
        {
            get { lock (_gate) { return _current; } }
        }

        public ListSyncRun? LastAttempt
        {
            get { lock (_gate) { return _last; } }
        }

        public Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
        {
            ListSyncRun.RequireLists(run, lists);
            lock (_gate)
            {
                var id = _history.Count + 1L;
                _history.Insert(0, new ListSyncAttempt(id, run with { Error = null, MissingInRules = null }));
                if (run.Outcome == ListSyncOutcome.Ok)
                {
                    _current = lists!;
                    _currentId = id;
                }

                _last = run;
            }

            return Task.CompletedTask;
        }

        public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Task.FromResult(LastAttempt);

        public Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                var matching = _history
                    .Where(a => (query.Triggers is null || query.Triggers.Contains(a.Run.Trigger))
                                && (query.Outcomes is null || query.Outcomes.Contains(a.Run.Outcome)))
                    .ToList();
                var items = matching.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
                return Task.FromResult(new ListSyncPage(items, matching.Count, _currentId));
            }
        }
    }
}
