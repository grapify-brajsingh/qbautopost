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

    /// <summary>M17 §4.7: QuickBooks has another company file open; nothing was sent.</summary>
    CompanyFileOpenElsewhere,

    /// <summary>M17 §4.7, ruling B1: the company file's session could not open; nothing was sent.</summary>
    CompanyFileUnavailable,

    /// <summary>
    /// M17 §4.7 (T-1708 ruling, carried to T-1712): the statement's company stopped being active + approved between the
    /// check and the claim. No session was opened and nothing was sent.
    /// </summary>
    CompanyNotReady,
}

/// <summary>An ending. <paramref name="Status"/> and <paramref name="Held"/> matter only for <see cref="PostEndingKind.Completed"/>.</summary>
public sealed record PostEnding(PostEndingKind Kind, JobStatus? Status, IReadOnlyList<PostHeldRow> Held);

/// <summary>A held row of a completed post: its reason code and the note that came with it (R31 reads both).</summary>
public sealed record PostHeldRow(string? Reason, string? Note);

/// <summary>The recorded outcome of an attempt, the request's next status, and the HTTP status answered (null when none was).</summary>
public sealed record AttemptVerdict(AttemptOutcome Outcome, SavedRequestStatus NextStatus, int? HttpStatus);
