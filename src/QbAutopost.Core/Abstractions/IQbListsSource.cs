using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Abstractions;

/// <summary>Whether a <see cref="IQbListsSource"/> has lists to offer, and the operator text that says so.</summary>
public sealed record QbListsStatus(bool Synced, string Message);

/// <summary>M14 §9: the current QuickBooks lists for readers. Memory reads only; never opens QuickBooks.</summary>
public interface IQbListsSource
{
    /// <summary>Never null; <see cref="QbLists.Empty"/> when nothing has been synced.</summary>
    QbLists Current { get; }

    /// <summary>The newest attempt of any outcome, or null when none is known.</summary>
    ListSyncRun? LastAttempt { get; }

    /// <summary>The one answer to "are the lists synced": <c>validate-company</c> and readiness both ask here.</summary>
    QbListsStatus Status() =>
        Current.SyncedUtc is { } synced
            ? new QbListsStatus(true, $"lists synced {synced.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)}")
            : new QbListsStatus(false, "no lists synced yet; run POST /api/v1/quickbooks/lists/sync");
}
