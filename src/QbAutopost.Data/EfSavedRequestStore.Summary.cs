using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using QbAutopost.Core.Dashboard;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>
/// T-1211: the summary facts (spec dashboard-api-m12 4.2). Event and posted facts read the view <c>audit_events</c>
/// through <see cref="Filter"/>, the filter of <see cref="AuditEventCountAsync"/>, so every time is the one the audit
/// feed shows (the <c>post-finished</c> event's <c>at_utc</c>) and the counts equal the audit totals. Post times are
/// <see cref="WithPostedUtc"/>, the SQL copy of <see cref="PostSummaries.Of"/>.
/// </summary>
public sealed partial class EfSavedRequestStore : IDashboardSummaryStore
{
    private static readonly string[] SummaryActions =
        [AuditActions.Received, AuditActions.Posted, AuditActions.PostFailed, AuditActions.Discarded];

    public async Task<IReadOnlyList<EventFact>> EventFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await Filter(db, FeedQuery(fromUtc, toUtc, SummaryActions))
            .Select(e => new { e.Action, e.AtUtc })
            .ToListAsync(ct);
        return rows.Select(e => new EventFact(e.Action, e.AtUtc)).ToList();
    }

    public async Task<IReadOnlyList<PostedFact>> PostedFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var events = await Filter(db, FeedQuery(fromUtc, toUtc, [AuditActions.Posted, AuditActions.PostFailed]))
            .Select(e => new { e.Action, e.AtUtc, e.Detail })
            .ToListAsync(ct);

        // A posted event, or a post-failed one whose outcome is partial (the view writes outcomes kebab-case).
        var finished = events
            .Select(e => (e.Action, e.AtUtc, Detail: ParseNode(e.Detail)))
            .Where(e => e.Action == AuditActions.Posted || DetailText(e.Detail, "outcome") == "partial")
            .Select(e => (AttemptId: DetailAttemptId(e.Detail), e.AtUtc))
            .Where(e => e.AttemptId is not null)
            .ToList();
        var ids = finished.Select(e => e.AttemptId!.Value).Distinct().ToList();
        var results = await db.PostAttempts.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Result })
            .ToDictionaryAsync(a => a.Id, a => a.Result, ct);

        return finished
            .Where(e => results.ContainsKey(e.AttemptId!.Value))
            .Select(e => new PostedFact(e.AttemptId!.Value, e.AtUtc, results[e.AttemptId!.Value]))
            .ToList();
    }

    public async Task<IReadOnlyList<PostTimeFact>> PostTimeFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await WithPostedUtc(db, db.SavedRequests.AsNoTracking())
            .Where(x => x.PostedUtc >= fromUtc && x.PostedUtc < toUtc)
            .Select(x => new { x.Row.Id, ReceivedUtc = x.Row.EmailReceivedUtc ?? x.Row.CreatedUtc, x.PostedUtc })
            .ToListAsync(ct);
        return rows.Select(r => new PostTimeFact(r.Id, r.ReceivedUtc, r.PostedUtc!.Value)).ToList();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>A request with its <c>postSummary.postedUtc</c>.</summary>
    private sealed class PostedRow
    {
        public SavedRequestEntity Row { get; init; } = null!;

        public DateTime? PostedUtc { get; init; }
    }

    /// <summary>
    /// <see cref="PostSummaries.Of"/> in SQL, for the summary, the list filter and the list sort: the finish of the
    /// latest (by id) posted or partial attempt; otherwise, for a posted request, the resolution time of the latest
    /// resolved attempt; otherwise null. Change both together (pinned by PostedFilterStoreContract).
    /// </summary>
    private static IQueryable<PostedRow> WithPostedUtc(QbAutopostDbContext db, IQueryable<SavedRequestEntity> rows) =>
        rows.Select(r => new PostedRow
        {
            Row = r,
            PostedUtc = db.PostAttempts.Any(a => a.SavedRequestId == r.Id
                                                 && (a.Outcome == AttemptOutcome.Posted || a.Outcome == AttemptOutcome.Partial))
                ? db.PostAttempts
                    .Where(a => a.SavedRequestId == r.Id
                                && (a.Outcome == AttemptOutcome.Posted || a.Outcome == AttemptOutcome.Partial))
                    .OrderByDescending(a => a.Id)
                    .Select(a => a.FinishedUtc)
                    .FirstOrDefault()
                : r.Status == SavedRequestStatus.Posted
                    ? db.PostAttempts
                        .Where(a => a.SavedRequestId == r.Id && a.ResolvedUtc != null)
                        .OrderByDescending(a => a.Id)
                        .Select(a => a.ResolvedUtc)
                        .FirstOrDefault()
                    : null,
        });

    private static AuditQuery FeedQuery(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> actions) =>
        new(fromUtc, toUtc, null, null, null, null, actions, null, IncludeUserEvents: false, 1, 1);

    private static string? DetailText(JsonNode? detail, string name) =>
        detail is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? DetailAttemptId(JsonNode? detail) =>
        detail is JsonObject o && o["attemptId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : null;
}
