using Microsoft.EntityFrameworkCore;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>
/// The dashboard v2 reads (T-1107, spec saved-requests-dashboard-v2 5.2 search, 5.3, 8.4, 9.1-9.3). Every value of a
/// query is a parameter. A page's history counts are one grouped count over the view <c>audit_events</c> with the
/// same filter as <see cref="AuditEventCountAsync"/>, and its post summaries one read of the attempts that can make one.
/// </summary>
public sealed partial class EfSavedRequestStore : ISavedRequestV2Store
{
    private static readonly SavedRequestStatus[] Unposted =
        [SavedRequestStatus.Pending, SavedRequestStatus.Posting, SavedRequestStatus.Partial, SavedRequestStatus.Unknown];

    public async Task<SavedRequest?> GetV2Async(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var record = await LoadAsync(db, id, ct);
        return record is null ? null : await WithPostSummaryAndHistoryAsync(db, record, ct);
    }

    public async Task<PagedResult<SavedRequestSummary>> ListV2Async(SavedRequestQueryV2 query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = FilterRequests(db, query.Base, v2: true);

        if (query.ReceivedFrom is { } from)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(r => (r.EmailReceivedUtc ?? r.CreatedUtc) >= fromUtc);
        }

        if (query.ReceivedTo is { } to)
        {
            var untilUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            rows = rows.Where(r => (r.EmailReceivedUtc ?? r.CreatedUtc) < untilUtc);
        }

        if (query.EmailFrom is { Length: > 0 } emailFrom)
        {
            var pattern = "%" + EscapeLike(emailFrom) + "%";
            rows = rows.Where(r => EF.Functions.ILike(r.EmailFrom!, pattern, LikeEscape));
        }

        if (query.Reference is { Length: > 0 } reference) rows = rows.Where(r => r.Reference == reference);
        if (query.MinTotal is { } min) rows = rows.Where(r => r.ControlTotal != null && r.ControlTotal >= min);
        if (query.MaxTotal is { } max) rows = rows.Where(r => r.ControlTotal != null && r.ControlTotal <= max);
        if (query.HasErrors is { } hasErrors) rows = rows.Where(r => (r.ProblemCount > 0 || r.NeedsAttentionV2) == hasErrors);
        if (query.ReceivedFromUtc is { } receivedFromUtc) rows = rows.Where(r => (r.EmailReceivedUtc ?? r.CreatedUtc) >= receivedFromUtc);
        if (query.ReceivedToUtc is { } receivedToUtc) rows = rows.Where(r => (r.EmailReceivedUtc ?? r.CreatedUtc) < receivedToUtc);
        if (query.CreatedFromUtc is { } createdFromUtc) rows = rows.Where(r => r.CreatedUtc >= createdFromUtc);
        if (query.CreatedToUtc is { } createdToUtc) rows = rows.Where(r => r.CreatedUtc < createdToUtc);
        if (query.PostedFromUtc is { } postedFromUtc)
            rows = WithPostedUtc(db, rows).Where(x => x.PostedUtc >= postedFromUtc).Select(x => x.Row);
        if (query.PostedToUtc is { } postedToUtc)
            rows = WithPostedUtc(db, rows).Where(x => x.PostedUtc < postedToUtc).Select(x => x.Row);

        var total = await rows.CountAsync(ct);
        var size = Math.Max(1, query.Base.PageSize);
        var page = Math.Max(1, query.Base.Page);
        var items = await OrderV2(db, rows, query.Base.Sort)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(r => new
            {
                r.Id, r.Status, r.Source, r.Reference, r.ControlTotal, r.TransactionCount, r.CurrentVersion, r.NeedsAttention,
                r.DuplicateOfId, r.DuplicateReasons, r.Validation, r.ValidatedUtc, r.PostId, r.CreatedUtc, r.CreatedBy,
                r.UpdatedUtc, r.UpdatedBy, r.RowVersion, r.EmailSubject, r.EmailReceivedUtc, r.EmailFrom, r.EmailMessageId,
                r.PushId, r.MetadataErrors, r.SkipCount, r.ValidationV2, r.NeedsAttentionV2, r.ProblemCount,
                r.UploadExtractionId, r.UploadFileName, r.UploadSizeBytes, r.UploadSha256, r.UploadModel,
                r.UploadPromptVersion, r.UploadExtractedUtc,
            })
            .ToListAsync(ct);

        var ids = items.Select(r => r.Id).ToList();
        var attempts = await PostSummaryAttemptsAsync(db, ids, ct);
        var history = await HistoryCountsAsync(db, ids, ct);
        var summaries = items.Select(r =>
        {
            var v2 = ReadSnapshot(r.ValidationV2);
            return new SavedRequestSummary(
                r.Id, r.Status, r.Source, r.Reference, r.ControlTotal, r.TransactionCount, r.CurrentVersion, r.NeedsAttention,
                r.DuplicateOfId, r.DuplicateReasons.ToList(), ReadSnapshot(r.Validation)?.State, r.ValidatedUtc, r.PostId,
                r.CreatedUtc, r.CreatedBy, r.UpdatedUtc, r.UpdatedBy, r.RowVersion)
            {
                Metadata = new SaveMetadata(
                    r.EmailSubject, r.EmailReceivedUtc, r.EmailFrom, r.EmailMessageId, r.PushId, r.MetadataErrors.ToList()),
                SkipCount = r.SkipCount,
                ValidationV2State = v2?.State,
                ValidationV2Utc = v2?.ValidatedUtc,
                ValidationV2Version = v2?.VersionNumber,
                NeedsAttentionV2 = r.NeedsAttentionV2,
                ProblemCount = r.ProblemCount,
                PostSummary = PostSummaries.Of(r.Status, r.PostId, attempts[r.Id]),
                HistoryCount = history.GetValueOrDefault(r.Id),
                Upload = UploadOf(r.UploadExtractionId, r.UploadFileName, r.UploadSizeBytes, r.UploadSha256, r.UploadModel,
                    r.UploadPromptVersion, r.UploadExtractedUtc),
            };
        }).ToList();
        return new PagedResult<SavedRequestSummary>(summaries, page, size, total);
    }

    public async Task<SavedRequestStatsV2> StatsV2Async(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var groups = await db.SavedRequests
            .GroupBy(r => r.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                Attention = g.Count(r => r.NeedsAttentionV2),
                Duplicate = g.Count(r => r.DuplicateReasons.Count > 0),
                Errors = g.Count(r => r.ProblemCount > 0 || r.NeedsAttentionV2),
            })
            .ToListAsync(ct);

        var byStatus = Enum.GetValues<SavedRequestStatus>()
            .ToDictionary(s => s, s => groups.Where(g => g.Status == s).Sum(g => g.Count));
        var pending = groups.SingleOrDefault(g => g.Status == SavedRequestStatus.Pending);
        return new SavedRequestStatsV2(
            groups.Sum(g => g.Count), byStatus,
            new FlagCounts(pending?.Attention ?? 0, groups.Sum(g => g.Attention)),
            new FlagCounts(pending?.Duplicate ?? 0, groups.Sum(g => g.Duplicate)),
            new FlagCounts(pending?.Errors ?? 0, groups.Sum(g => g.Errors)));
    }

    public async Task<IReadOnlyList<OtherPush>> OtherUnpostedPushesAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var reference = await db.SavedRequests.Where(r => r.Id == id).Select(r => r.Reference).SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(reference))
        {
            return [];
        }

        var statuses = Unposted.ToList();
        var others = await db.SavedRequests.AsNoTracking()
            .Where(r => r.Id != id && r.Reference == reference && statuses.Contains(r.Status))
            .OrderBy(r => r.EmailReceivedUtc ?? r.CreatedUtc).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.PushId, ReceivedUtc = r.EmailReceivedUtc ?? r.CreatedUtc, r.Status })
            .ToListAsync(ct);
        return others.Select(o => new OtherPush(o.Id, o.PushId, o.ReceivedUtc, o.Status)).ToList();
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    /// <summary>What <see cref="GetV2Async"/> adds to <see cref="ISavedRequestStore.GetAsync"/>'s record: the post summary and the history count.</summary>
    private static async Task<SavedRequest> WithPostSummaryAndHistoryAsync(QbAutopostDbContext db, SavedRequest record, CancellationToken ct)
    {
        List<long> ids = [record.Id];
        var attempts = await PostSummaryAttemptsAsync(db, ids, ct);
        var history = await HistoryCountsAsync(db, ids, ct);
        return record with
        {
            PostSummary = PostSummaries.Of(record.Status, record.PostId, attempts[record.Id]),
            HistoryCount = history.GetValueOrDefault(record.Id),
        };
    }

    /// <summary>
    /// The attempts of <paramref name="ids"/> that <see cref="PostSummaries.Of"/> can use (posted, partial or resolved),
    /// with only the columns it reads.
    /// </summary>
    private static async Task<ILookup<long, PostAttempt>> PostSummaryAttemptsAsync(
        QbAutopostDbContext db, List<long> ids, CancellationToken ct)
    {
        var rows = await db.PostAttempts.AsNoTracking()
            .Where(a => ids.Contains(a.SavedRequestId)
                        && (a.Outcome == AttemptOutcome.Posted || a.Outcome == AttemptOutcome.Partial || a.ResolvedUtc != null))
            .Select(a => new
            {
                a.Id, a.SavedRequestId, a.VersionNumber, a.Outcome, a.PostId, a.Counts, a.StartedUtc, a.FinishedUtc,
                a.StartedBy, a.ResolvedBy, a.ResolvedUtc,
            })
            .ToListAsync(ct);
        return rows.ToLookup(
            a => a.SavedRequestId,
            a => new PostAttempt(
                a.Id, a.SavedRequestId, a.VersionNumber, a.Outcome, null, a.PostId, null, ParseNode(a.Counts), null,
                a.StartedUtc, a.FinishedUtc, a.StartedBy, a.ResolvedBy, a.ResolvedUtc, null));
    }

    /// <summary>
    /// <c>audit_events</c> rows per request of <paramref name="ids"/>, counted with the filter of
    /// <see cref="AuditEventCountAsync"/> for a request id without user events (spec 8.4).
    /// </summary>
    private static async Task<Dictionary<long, int>> HistoryCountsAsync(QbAutopostDbContext db, List<long> ids, CancellationToken ct)
    {
        var requestIds = ids.Select(id => (long?)id).ToList();
        var everyRequest = new AuditQuery(null, null, null, null, null, null, [], null, IncludeUserEvents: false, 1, 1);
        var counts = await Filter(db, everyRequest)
            .Where(e => requestIds.Contains(e.RequestId))
            .GroupBy(e => e.RequestId)
            .Select(g => new { RequestId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return counts.ToDictionary(c => c.RequestId!.Value, c => c.Count);
    }

    private static IQueryable<SavedRequestEntity> OrderV2(QbAutopostDbContext db, IQueryable<SavedRequestEntity> rows, string sort)
    {
        var descending = sort.StartsWith('-');
        return sort.TrimStart('-') switch
        {
            // T-1211: no post time last in both directions (NULLS LAST), then id desc, as in the fake.
            "posted" => descending
                ? WithPostedUtc(db, rows).OrderBy(x => x.PostedUtc == null).ThenByDescending(x => x.PostedUtc)
                    .ThenByDescending(x => x.Row.Id).Select(x => x.Row)
                : WithPostedUtc(db, rows).OrderBy(x => x.PostedUtc == null).ThenBy(x => x.PostedUtc)
                    .ThenByDescending(x => x.Row.Id).Select(x => x.Row),
            "received" => descending
                ? rows.OrderByDescending(r => r.EmailReceivedUtc ?? r.CreatedUtc).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.EmailReceivedUtc ?? r.CreatedUtc).ThenBy(r => r.Id),
            "total" => Order(rows, descending ? "-controlTotal" : "controlTotal"),
            "rows" => descending
                ? rows.OrderByDescending(r => r.TransactionCount).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.TransactionCount).ThenBy(r => r.Id),
            _ => Order(rows, sort),
        };
    }
}
