using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.SavedRequests;

/// <summary>Maps a post ending to an attempt outcome and the request's next status (spec §7.2).</summary>
public static class AttemptOutcomes
{
    public static AttemptVerdict From(PostEnding ending) => ending.Kind switch
    {
        PostEndingKind.Unreadable or PostEndingKind.Refused =>
            new(AttemptOutcome.Refused, SavedRequestStatus.Pending, 400),
        PostEndingKind.Held => new(AttemptOutcome.Held, SavedRequestStatus.Pending, 422),
        PostEndingKind.Busy => new(AttemptOutcome.Busy, SavedRequestStatus.Pending, 409),
        PostEndingKind.Unavailable => new(AttemptOutcome.Unavailable, SavedRequestStatus.Pending, 503),
        PostEndingKind.ErrorBeforeSend => new(AttemptOutcome.Error, SavedRequestStatus.Pending, 500),
        PostEndingKind.ErrorAfterSend => new(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, null),
        PostEndingKind.Completed => FromCompleted(ending),
        _ => new(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, null),
    };

    private static AttemptVerdict FromCompleted(PostEnding ending) => ending.Status switch
    {
        JobStatus.Posted => new(AttemptOutcome.Posted, SavedRequestStatus.Posted, 200),
        // Partial already locks the request, so a no-response row inside it changes nothing.
        JobStatus.Partial => new(AttemptOutcome.Partial, SavedRequestStatus.Partial, 200),
        // Failed alone cannot tell "nothing landed" from "nothing confirmed": a no-response row means the qbXML was sent.
        JobStatus.Failed when ending.HeldReasons.Contains(HoldReasons.QuickBooksNoResponse) =>
            new(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, 200),
        JobStatus.Failed => new(AttemptOutcome.Failed, SavedRequestStatus.Pending, 200),
        // SPEC-GAP T-1002: a completed ending with no or a non-terminal status is unexplained; lock rather than allow a re-post.
        _ => new(AttemptOutcome.Unknown, SavedRequestStatus.Unknown, 200),
    };
}
