namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>Filters of the audit feed (spec saved-requests-dashboard-v2 8.1). Null or empty means no filter.</summary>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive.</param>
/// <param name="Actor">Exact actor id: a user id, <c>api-client:&lt;id&gt;</c> or <c>system</c>.</param>
/// <param name="Reference">Exact match on the request's current reference.</param>
/// <param name="Actions">Any of these; empty means every action.</param>
/// <param name="Q">Case-insensitive contains over the JSON text of old and new, field, reference, public reference and push id hex.</param>
/// <param name="IncludeUserEvents">False drops every <c>user_audit</c> row, from the items and from the total.</param>
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
    int PageSize);
