using System.Text.Json.Nodes;
using QbAutopost.Core.Dashboard;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// T-1211: the summary facts of the fake (spec dashboard-api-m12 4.2), read from the fake's own audit feed, as the EF
/// store reads the view <c>audit_events</c>; post times are <see cref="PostSummaries.Of"/>.
/// </summary>
public sealed partial class FakeSavedRequestStore : IDashboardSummaryStore
{
    private static readonly string[] SummaryActions =
        [AuditActions.Received, AuditActions.Posted, AuditActions.PostFailed, AuditActions.Discarded];

    public Task<IReadOnlyList<EventFact>> EventFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<EventFact> facts = AuditRows(FeedQuery(fromUtc, toUtc, SummaryActions))
                .Select(e => new EventFact(e.Action, e.AtUtc))
                .ToList();
            return Task.FromResult(facts);
        }
    }

    public Task<IReadOnlyList<PostedFact>> PostedFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var attempts = _attempts.Values.SelectMany(a => a).ToDictionary(a => a.Id);
            IReadOnlyList<PostedFact> facts = AuditRows(FeedQuery(fromUtc, toUtc, [AuditActions.Posted, AuditActions.PostFailed]))
                .Where(e => e.Action == AuditActions.Posted || Text(e.Detail, "outcome") == "partial")
                .Select(e => AttemptIdOf(e.Detail) is { } id && attempts.TryGetValue(id, out var a)
                    ? new PostedFact(a.Id, e.AtUtc, a.Result?.ToJsonString())
                    : null)
                .OfType<PostedFact>()
                .ToList();
            return Task.FromResult(facts);
        }
    }

    public Task<IReadOnlyList<PostTimeFact>> PostTimeFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<PostTimeFact> facts = _requests.Values
                .Select(r => (r, At: PostedUtcOf(r)))
                .Where(x => x.At is { } at && at >= fromUtc && at < toUtc)
                .Select(x => new PostTimeFact(x.r.Id, ReceivedUtc(x.r), x.At!.Value))
                .ToList();
            return Task.FromResult(facts);
        }
    }

    // ---- helpers (call only inside the lock) --------------------------------------------------------------------

    /// <summary><c>postSummary.postedUtc</c>: the one post time of the summary, the list filter and the list sort.</summary>
    private DateTime? PostedUtcOf(SavedRequest r) => PostSummaries.Of(r.Status, r.PostId, _attempts[r.Id])?.PostedUtc;

    private static AuditQuery FeedQuery(DateTime fromUtc, DateTime toUtc, IReadOnlyList<string> actions) =>
        new(fromUtc, toUtc, null, null, null, null, actions, null, IncludeUserEvents: false, 1, 1);

    private static string? Text(JsonNode? detail, string name) =>
        detail is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? AttemptIdOf(JsonNode? detail) =>
        detail is JsonObject o && o["attemptId"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : null;
}
