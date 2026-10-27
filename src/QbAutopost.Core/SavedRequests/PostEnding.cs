using QbAutopost.Core.Jobs;

namespace QbAutopost.Core.SavedRequests;

/// <summary>How a validate-and-post call ended, as seen by the saved-request layer.</summary>
public enum PostEndingKind
{
    Unreadable,
    Refused,
    Held,
    Busy,
    Unavailable,
    Completed,
    ErrorBeforeSend,
    ErrorAfterSend,
}

/// <summary>An ending. <paramref name="Status"/> and <paramref name="HeldReasons"/> matter only for <see cref="PostEndingKind.Completed"/>.</summary>
public sealed record PostEnding(PostEndingKind Kind, JobStatus? Status, IReadOnlyList<string> HeldReasons);

/// <summary>The recorded outcome of an attempt, the request's next status, and the HTTP status answered (null when none was).</summary>
public sealed record AttemptVerdict(AttemptOutcome Outcome, SavedRequestStatus NextStatus, int? HttpStatus);
