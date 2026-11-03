using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Abstractions;

/// <summary>M14 §9: the current QuickBooks lists for readers. Memory reads only; never opens QuickBooks.</summary>
public interface IQbListsSource
{
    /// <summary>Never null; <see cref="QbLists.Empty"/> when nothing has been synced.</summary>
    QbLists Current { get; }

    /// <summary>The newest attempt of any outcome, or null when none is known.</summary>
    ListSyncRun? LastAttempt { get; }
}
