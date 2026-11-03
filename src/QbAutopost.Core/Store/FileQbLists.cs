using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Store;

/// <summary>
/// File mode (M14 §3.4, §9): <c>qb-lists.json</c> is the current lists, read on each <see cref="Current"/> as readers
/// always did. Only an Ok run writes it; the last attempt is kept in memory for every run.
/// </summary>
public sealed class FileQbLists(string filePath) : IQbListsStore, IQbListsSource
{
    private readonly QbListsStore _file = new(filePath);
    private ListSyncRun? _last;

    public QbLists Current => _file.Load();

    public ListSyncRun? LastAttempt => Volatile.Read(ref _last);

    public Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct)
    {
        if (run.Outcome == ListSyncOutcome.Ok && lists is not null)
        {
            _file.Save(lists);
        }

        Volatile.Write(ref _last, run);
        return Task.CompletedTask;
    }

    public Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct) => Task.FromResult(LastAttempt);
}
