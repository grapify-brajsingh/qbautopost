using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests;

/// <summary>The <c>postSummary</c> rule of spec saved-requests-dashboard-v2 9.1, shared by both stores.</summary>
public static class PostSummaries
{
    /// <summary>
    /// From the latest attempt with outcome posted or partial: its post id, <c>counts.posted</c>, finish time and
    /// starter. Otherwise, when the request is posted (settled by resolve), the request's post id and the latest
    /// resolved attempt: no count, the resolution time and the resolver. Otherwise null.
    /// </summary>
    public static PostSummary? Of(SavedRequestStatus status, string? requestPostId, IEnumerable<PostAttempt> attempts)
    {
        var list = attempts.ToList();
        var posted = list
            .Where(a => a.Outcome is AttemptOutcome.Posted or AttemptOutcome.Partial)
            .MaxBy(a => a.Id);
        if (posted is not null)
        {
            return new PostSummary(posted.PostId, PostedCount(posted.Counts), posted.FinishedUtc, posted.StartedBy);
        }

        if (status != SavedRequestStatus.Posted)
        {
            return null;
        }

        var resolved = list.Where(a => a.ResolvedUtc is not null).MaxBy(a => a.Id);
        return resolved is null ? null : new PostSummary(requestPostId, null, resolved.ResolvedUtc, resolved.ResolvedBy);
    }

    private static int? PostedCount(JsonNode? counts) =>
        counts is JsonObject o && o["posted"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
