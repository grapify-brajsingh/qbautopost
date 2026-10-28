namespace QbAutopost.Core.Dashboard;

/// <summary>An audit event the summary counts: one of <c>received</c>, <c>posted</c>, <c>post-failed</c>, <c>discarded</c>.</summary>
public sealed record EventFact(string Action, DateTime AtUtc);

/// <summary>
/// An attempt that finished posted or partial. <see cref="FinishedUtc"/> is its <c>post-finished</c> event's time, the
/// one the audit feed uses; <see cref="ResultJson"/> is the stored result as it was written.
/// </summary>
public sealed record PostedFact(long AttemptId, DateTime FinishedUtc, string? ResultJson);

/// <summary>
/// A request with a post time. <see cref="ReceivedUtc"/> is <c>coalesce(email_received_utc, created_utc)</c>;
/// <see cref="PostedUtc"/> is <c>PostSummaries.Of(...).PostedUtc</c>.
/// </summary>
public sealed record PostTimeFact(long SavedRequestId, DateTime ReceivedUtc, DateTime PostedUtc);

/// <summary>The reads behind <c>GET /api/v2/dashboard/summary</c> (spec dashboard-api-m12 4.2). Every range is [from, to).</summary>
public interface IDashboardSummaryStore
{
    /// <summary>
    /// The <c>received</c>, <c>posted</c>, <c>post-failed</c> and <c>discarded</c> audit events in the range, read from the
    /// same source as <c>audit_events</c>, so their counts equal <c>AuditEventCountAsync</c> for the same filter.
    /// </summary>
    Task<IReadOnlyList<EventFact>> EventFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>The attempts that finished posted or partial, by their <c>post-finished</c> event time in the range.</summary>
    Task<IReadOnlyList<PostedFact>> PostedFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>The requests whose <c>PostSummaries.Of</c> post time is in the range.</summary>
    Task<IReadOnlyList<PostTimeFact>> PostTimeFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}
