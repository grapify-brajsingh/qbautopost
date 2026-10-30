using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Abstractions;

/// <summary>M14 §3/§4: where list sync attempts are recorded and the current lists are kept.</summary>
public interface IQbListsStore
{
    /// <summary>
    /// Records <paramref name="run"/>. <paramref name="lists"/> is non-null only when <c>run.Outcome == Ok</c>; an
    /// implementation must never replace the current lists on a run that is not Ok.
    /// </summary>
    Task RecordAsync(ListSyncRun run, QbLists? lists, CancellationToken ct);

    /// <summary>The newest attempt of any outcome; null when none is known.</summary>
    Task<ListSyncRun?> LastAttemptAsync(CancellationToken ct);

    /// <summary>
    /// M15 §3.1: attempts newest first, filtered, one page; total counts the filtered set. Never reads the error, the
    /// names missing in the rules or the stored lists.
    /// </summary>
    Task<ListSyncPage> ListAttemptsAsync(ListSyncQuery query, CancellationToken ct);
}
