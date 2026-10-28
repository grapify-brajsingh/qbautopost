using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// T-1107: the dashboard v2 reads of the fake (spec saved-requests-dashboard-v2 5.2 search, 5.3, 8.4, 9.1-9.3), with
/// the filters, sorts and counts of the EF Core store. History counts come from the fake's own audit feed.
/// </summary>
public sealed partial class FakeSavedRequestStore : ISavedRequestV2Store
{
    private static readonly SavedRequestStatus[] Unposted =
        [SavedRequestStatus.Pending, SavedRequestStatus.Posting, SavedRequestStatus.Partial, SavedRequestStatus.Unknown];

    public Task<PagedResult<SavedRequestSummary>> ListV2Async(SavedRequestQueryV2 query, CancellationToken ct)
    {
        lock (_gate)
        {
            var rows = Filter(_requests.Values, query.Base, v2: true);
            if (query.ReceivedFrom is { } from) rows = rows.Where(r => DateOnly.FromDateTime(ReceivedUtc(r)) >= from);
            if (query.ReceivedTo is { } to) rows = rows.Where(r => DateOnly.FromDateTime(ReceivedUtc(r)) <= to);
            if (query.EmailFrom is { Length: > 0 } emailFrom)
                rows = rows.Where(r => r.Metadata.EmailFrom?.Contains(emailFrom, StringComparison.OrdinalIgnoreCase) ?? false);
            if (query.Reference is { Length: > 0 } reference) rows = rows.Where(r => r.Reference == reference);
            if (query.MinTotal is { } min) rows = rows.Where(r => r.ControlTotal is { } t && t >= min);
            if (query.MaxTotal is { } max) rows = rows.Where(r => r.ControlTotal is { } t && t <= max);
            if (query.HasErrors is { } hasErrors) rows = rows.Where(r => HasErrors(r) == hasErrors);

            var ordered = OrderV2(rows, query.Base.Sort).ToList();
            var size = Math.Max(1, query.Base.PageSize);
            var page = Math.Max(1, query.Base.Page);
            var items = ordered.Skip((page - 1) * size).Take(size).Select(SummarizeV2).ToList();
            return Task.FromResult(new PagedResult<SavedRequestSummary>(items, page, size, ordered.Count));
        }
    }

    public Task<SavedRequestStatsV2> StatsV2Async(CancellationToken ct)
    {
        lock (_gate)
        {
            var all = _requests.Values.ToList();
            var byStatus = Enum.GetValues<SavedRequestStatus>().ToDictionary(s => s, s => all.Count(r => r.Status == s));
            return Task.FromResult(new SavedRequestStatsV2(
                all.Count, byStatus,
                Flag(all, r => r.NeedsAttentionV2),
                Flag(all, r => r.DuplicateReasons.Count > 0),
                Flag(all, HasErrors)));
        }

        static FlagCounts Flag(List<SavedRequest> all, Func<SavedRequest, bool> flag) =>
            new(all.Count(r => r.Status == SavedRequestStatus.Pending && flag(r)), all.Count(flag));
    }

    public Task<IReadOnlyList<OtherPush>> OtherUnpostedPushesAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var self) || self.Reference is not { Length: > 0 } reference)
                return Task.FromResult<IReadOnlyList<OtherPush>>([]);

            IReadOnlyList<OtherPush> others = _requests.Values
                .Where(r => r.Id != id && r.Reference == reference && Unposted.Contains(r.Status))
                .OrderBy(ReceivedUtc).ThenBy(r => r.Id)
                .Select(r => new OtherPush(r.Id, r.Metadata.PushId, ReceivedUtc(r), r.Status))
                .ToList();
            return Task.FromResult(others);
        }
    }

    // ---- helpers (call only inside the lock) --------------------------------------------------------------------

    private SavedRequest WithPostSummaryAndHistory(SavedRequest view) => view with
    {
        PostSummary = PostSummaries.Of(view.Status, view.PostId, _attempts[view.Id]),
        HistoryCount = HistoryCount(view.Id),
    };

    private int HistoryCount(long id) =>
        AuditRows(new AuditQuery(null, null, null, id, null, null, [], null, false, 1, 1)).Count();

    private SavedRequestSummary SummarizeV2(SavedRequest r) => Summarize(r) with
    {
        Metadata = Copy(r.Metadata),
        SkipCount = r.SkipCount,
        ValidationV2State = r.ValidationV2?.State,
        ValidationV2Utc = r.ValidationV2?.ValidatedUtc,
        ValidationV2Version = r.ValidationV2?.VersionNumber,
        NeedsAttentionV2 = r.NeedsAttentionV2,
        ProblemCount = r.ProblemCount,
        PostSummary = PostSummaries.Of(r.Status, r.PostId, _attempts[r.Id]),
        HistoryCount = HistoryCount(r.Id),
    };

    private static DateTime ReceivedUtc(SavedRequest r) => r.Metadata.EmailReceivedUtc ?? r.CreatedUtc;

    private static bool HasErrors(SavedRequest r) => r.ProblemCount > 0 || r.NeedsAttentionV2;

    /// <summary>The v2 additions to <c>q</c>: the email subject (contains) and, when <paramref name="q"/> without dashes is 1-32 hex digits, the push id prefix.</summary>
    private static bool MatchesV2(SavedRequest r, string q) =>
        (r.Metadata.EmailSubject?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (SavedRequestSearch.PushIdHexPrefix(q) is { } prefix
            && (r.Metadata.PushId?.ToString("N").StartsWith(prefix, StringComparison.Ordinal) ?? false));

    private static IEnumerable<SavedRequest> OrderV2(IEnumerable<SavedRequest> rows, string sort)
    {
        var descending = sort.StartsWith('-');
        return sort.TrimStart('-') switch
        {
            "received" => descending
                ? rows.OrderByDescending(ReceivedUtc).ThenByDescending(r => r.Id)
                : rows.OrderBy(ReceivedUtc).ThenBy(r => r.Id),
            "total" => Order(rows, descending ? "-controlTotal" : "controlTotal"),
            "rows" => descending
                ? rows.OrderByDescending(r => r.TransactionCount).ThenByDescending(r => r.Id)
                : rows.OrderBy(r => r.TransactionCount).ThenBy(r => r.Id),
            _ => Order(rows, sort),
        };
    }
}
