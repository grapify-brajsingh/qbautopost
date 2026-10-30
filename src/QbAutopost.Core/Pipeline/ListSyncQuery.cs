namespace QbAutopost.Core.Pipeline;

/// <summary>
/// M15 §3.1: one page of the list sync history. A null set means any value; <see cref="Page"/> is 1-based. Validated by
/// the caller (the route answers 400 before a query is made).
/// </summary>
public sealed record ListSyncQuery(
    IReadOnlySet<ListSyncTrigger>? Triggers,
    IReadOnlySet<ListSyncOutcome>? Outcomes,
    int Page,
    int PageSize);
