namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// The dashboard v2 reads (T-1107, spec saved-requests-dashboard-v2 9): the list with its extra filters and sorts,
/// the stats, and the other unposted pushes of a reference. Implemented by every <see cref="ISavedRequestStore"/>
/// (the fake, the EF Core store, the disabled store) and kept off that interface, whose method list is pinned by
/// <c>EfSavedRequestStoreTests.Should_ExposeNoVersionMutation_When_InspectingStoreContract</c> (owner ruling O-1).
/// Both stores must pass <c>ListV2StoreContract</c>.
/// </summary>
public interface ISavedRequestV2Store
{
    /// <summary>
    /// <see cref="ISavedRequestStore.GetAsync"/>'s record plus <see cref="SavedRequest.PostSummary"/> and
    /// <see cref="SavedRequest.HistoryCount"/> (controller ruling R7: only the v2 detail reads these, so v1 reads never
    /// depend on the <c>audit_events</c> view).
    /// </summary>
    Task<SavedRequest?> GetV2Async(long id, CancellationToken ct);

    /// <summary>
    /// One page of v2 list rows, with <see cref="SavedRequestSummary.PostSummary"/> and
    /// <see cref="SavedRequestSummary.HistoryCount"/> filled, and how many match in all. The <c>id</c> tie-break of
    /// every sort is kept.
    /// </summary>
    Task<PagedResult<SavedRequestSummary>> ListV2Async(SavedRequestQueryV2 query, CancellationToken ct);

    /// <summary>The v2 stats, over the statements <paramref name="company"/> selects (null: all of them).</summary>
    Task<SavedRequestStatsV2> StatsV2Async(CompanyFilter? company, CancellationToken ct);

    /// <summary>
    /// Other requests with the same non-empty reference as <paramref name="id"/> and status pending, posting, partial
    /// or unknown, ordered by received time (then id). Empty when the request is unknown or has no reference.
    /// </summary>
    Task<IReadOnlyList<OtherPush>> OtherUnpostedPushesAsync(long id, CancellationToken ct);

    /// <summary>
    /// How many pending requests each company has (company id to count; companies with none are absent, requests with
    /// no company are not counted). The dashboard's "waiting" figure on the company list (M17 spec 5.1).
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> PendingByCompanyAsync(CancellationToken ct);
}
