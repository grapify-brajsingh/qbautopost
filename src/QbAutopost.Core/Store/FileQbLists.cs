using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Store;

/// <summary>
/// File mode (M14 §3.4, §9): <c>qb-lists.json</c> is the current lists, read on each <see cref="Current"/> as readers
/// always did. Only an Ok run writes it; the last attempt is kept in memory for every run.
/// <para>
/// M15 §3 (Q-125): the attempts since the API started are kept in memory too, newest <paramref name="historyRows"/> only
/// (<c>QuickBooks:ListSync:HistoryRows</c>), numbered by a per-process sequence, without their error or missing names.
/// </para>
/// </summary>
public sealed class FileQbLists(string filePath, int historyRows = FileQbLists.DefaultHistoryRows) : IQbListsStore, IQbListsSource
{
    public const int DefaultHistoryRows = 200;

    private readonly QbListsStore _file = new(filePath);
    private readonly int _historyRows = historyRows > 0 ? historyRows : throw new ArgumentOutOfRangeException(nameof(historyRows));
    private readonly object _gate = new();
    private readonly LinkedList<ListSyncAttempt> _history = new();
    private long _nextId;
    private long? _currentId;
    private ListSyncRun? _last;

    public QbLists Current => _file.Load();

    public ListSyncRun? LastAttempt => Volatile.Read(ref _last);

    public Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
    {
        if (run.Outcome == ListSyncOutcome.Ok && lists is not null)
        {
            _file.Save(lists);
        }

        lock (_gate)
        {
            var id = ++_nextId;
            _history.AddFirst(new ListSyncAttempt(id, run with { Error = null, MissingInRules = null }));
            if (run.Outcome == ListSyncOutcome.Ok)
            {
                _currentId = id;
            }

            while (_history.Count > _historyRows)
            {
                _history.RemoveLast();
            }
        }

        Volatile.Write(ref _last, run);
        return Task.CompletedTask;
    }

    public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Task.FromResult(LastAttempt);

    public Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            var matching = _history
                .Where(a => (query.Triggers is null || query.Triggers.Contains(a.Run.Trigger))
                            && (query.Outcomes is null || query.Outcomes.Contains(a.Run.Outcome)))
                .ToList();
            var items = matching.Skip((int)Math.Min(int.MaxValue, (long)(query.Page - 1) * query.PageSize)).Take(query.PageSize).ToList();
            return Task.FromResult(new ListSyncPage(items, matching.Count, _currentId));
        }
    }
}
