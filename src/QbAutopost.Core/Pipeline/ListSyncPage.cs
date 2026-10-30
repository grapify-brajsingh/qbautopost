namespace QbAutopost.Core.Pipeline;

/// <summary>
/// M15 §3.1: attempts newest first; <see cref="Total"/> counts the filtered set; <see cref="CurrentId"/> is the id of the
/// newest Ok attempt of the whole history (its lists are the ones in use), whatever the filter, or null when none.
/// </summary>
public sealed record ListSyncPage(IReadOnlyList<ListSyncAttempt> Items, int Total, long? CurrentId);
