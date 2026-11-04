namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>Filters of the audit feed (spec saved-requests-dashboard-v2 8.1). Null or empty means no filter.</summary>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive.</param>
/// <param name="Actor">Exact actor id: a user id, <c>api-client:&lt;id&gt;</c> or <c>system</c>.</param>
/// <param name="Reference">Exact match on the request's current reference.</param>
/// <param name="Actions">Any of these; empty means every action.</param>
/// <param name="Q">Case-insensitive contains over the JSON text of old and new, field, reference, public reference and push id hex.</param>
/// <param name="IncludeUserEvents">
/// False drops the user events (id prefix <c>u</c>: the people-management rows), from the items and from the total. The
/// company events (prefix <c>c</c>, M17) are operational, not user management: every caller who may read the feed sees them.
/// </param>
public sealed record AuditQuery(
    DateTime? From,
    DateTime? To,
    string? Actor,
    long? RequestId,
    string? Reference,
    Guid? PushId,
    IReadOnlyList<string> Actions,
    string? Q,
    bool IncludeUserEvents,
    int Page,
    int PageSize)
{
    /// <summary>
    /// M17 spec 5.3: only events of this company (the request's company, or the company a company event is about);
    /// null is every company.
    /// </summary>
    public int? CompanyId { get; init; }
}
