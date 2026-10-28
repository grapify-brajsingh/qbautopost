namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>
/// The audit feed (T-1106, spec saved-requests-dashboard-v2 8): timeline events, version changes and
/// <c>user_audit</c> rows mapped by <see cref="AuditEventMapper"/>'s table. Reads only. Implemented by every
/// <see cref="ISavedRequestStore"/> (the fake, the EF Core store, the disabled store) and kept off that interface,
/// whose method list is pinned by <c>EfSavedRequestStoreTests.Should_ExposeNoVersionMutation_When_InspectingStoreContract</c>
/// (existing tests stay unedited, owner ruling O-1). Both must pass <c>AuditEventsStoreContract</c>.
/// </summary>
public interface ISavedRequestAuditStore
{
    /// <summary>
    /// The rows matching <paramref name="query"/>, newest first (<c>atUtc desc</c>, then <c>id desc</c> compared as
    /// ordinal text), one page of them, and how many match in all.
    /// </summary>
    Task<PagedResult<AuditEvent>> AuditEventsAsync(AuditQuery query, CancellationToken ct);

    /// <summary>How many rows <see cref="AuditEventsAsync"/> matches for <paramref name="query"/>, ignoring paging.</summary>
    Task<int> AuditEventCountAsync(AuditQuery query, CancellationToken ct);
}
