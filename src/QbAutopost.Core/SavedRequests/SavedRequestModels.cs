using System.Text.Json;
using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests;

/// <summary>Event kinds written to the timeline (spec §5.2).</summary>
public static class SavedRequestEvents
{
    public const string Created = "created";
    public const string FlaggedDuplicate = "flagged-duplicate";
    public const string Edited = "edited";
    public const string Validated = "validated";
    public const string PostStarted = "post-started";
    public const string PostFinished = "post-finished";
    public const string Discarded = "discarded";
    public const string Restored = "restored";
    public const string Resolved = "resolved";
}

public enum CreateKind { Created, Replayed, KeyReused }

public enum MutationKind { Ok, NotFound, Stale, WrongStatus, Unchanged }

public enum ClaimKind { Claimed, NotFound, Stale, AlreadyPosting, AlreadyPosted, NotPostable }

public sealed record CreateResult(CreateKind Kind, SavedRequest Record);

public sealed record MutationResult(MutationKind Kind, SavedRequest? Record);

public sealed record ClaimResult(ClaimKind Kind, long? AttemptId, int VersionNumber, SavedRequest? Record);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record NewSavedRequest(
    string Source,
    string CreatedBy,
    string OriginalBody,
    JsonElement Payload,
    PayloadRead Read,
    string CanonicalSha256,
    IReadOnlySet<string> RowFingerprints,
    string? IdempotencyKey,
    ValidationSnapshot Snapshot,
    DuplicateMatch Duplicate,
    DateTime NowUtc);

public sealed record NewVersion(
    JsonElement Payload,
    PayloadRead Read,
    string CanonicalSha256,
    IReadOnlySet<string> RowFingerprints,
    IReadOnlyList<PayloadChange> Changes,
    string? Note,
    ValidationSnapshot Snapshot,
    DuplicateMatch Duplicate,
    string Actor,
    DateTime NowUtc);

public sealed record AttemptCompletion(
    AttemptOutcome Outcome,
    SavedRequestStatus NextStatus,
    int? HttpStatus,
    string? PostId,
    JsonNode? Result,
    JsonNode? Counts,
    string? Error,
    DateTime FinishedUtc);

/// <param name="Statuses">Empty means every status.</param>
/// <param name="Q">Matches reference, public reference, and payee or memo text inside the payload (case-insensitive).</param>
/// <param name="Sort"><c>created</c> | <c>updated</c> | <c>controlTotal</c>; a leading <c>-</c> means descending.</param>
public sealed record SavedRequestQuery(
    IReadOnlyList<SavedRequestStatus> Statuses,
    bool? NeedsAttention,
    bool? Duplicate,
    string? Q,
    DateOnly? CreatedFrom,
    DateOnly? CreatedTo,
    string? Source,
    string Sort,
    int Page,
    int PageSize);

public sealed record SavedRequestStats(
    int Total,
    IReadOnlyDictionary<SavedRequestStatus, int> ByStatus,
    int NeedsAttention,
    int Duplicates);

/// <summary>A saved request: every spec §5.2 column, plus the latest payload and the latest attempt.</summary>
public sealed record SavedRequest(
    long Id,
    SavedRequestStatus Status,
    string Source,
    string? Reference,
    decimal? ControlTotal,
    int TransactionCount,
    int CurrentVersion,
    string OriginalBody,
    string OriginalSha256,
    string CanonicalSha256,
    string? IdempotencyKey,
    bool NeedsAttention,
    long? DuplicateOfId,
    IReadOnlyList<string> DuplicateReasons,
    string? DuplicateAcknowledgedBy,
    DateTime? DuplicateAcknowledgedUtc,
    ValidationSnapshot? Validation,
    DateTime? ValidatedUtc,
    string? PostId,
    string? DiscardReason,
    DateTime CreatedUtc,
    string CreatedBy,
    DateTime UpdatedUtc,
    string UpdatedBy,
    int RowVersion,
    JsonElement CurrentPayload,
    PostAttempt? LastAttempt)
{
    public string PublicRef => $"SR-{Id:000000}";
}

/// <summary>A list row: everything except the payload, the validation detail and the read errors (spec §6.5).</summary>
public sealed record SavedRequestSummary(
    long Id,
    SavedRequestStatus Status,
    string Source,
    string? Reference,
    decimal? ControlTotal,
    int TransactionCount,
    int CurrentVersion,
    bool NeedsAttention,
    long? DuplicateOfId,
    IReadOnlyList<string> DuplicateReasons,
    ValidationState? ValidationState,
    DateTime? ValidatedUtc,
    string? PostId,
    DateTime CreatedUtc,
    string CreatedBy,
    DateTime UpdatedUtc,
    string UpdatedBy,
    int RowVersion)
{
    public string PublicRef => $"SR-{Id:000000}";
}

public sealed record SavedRequestVersion(
    long Id,
    long SavedRequestId,
    int Number,
    JsonElement Payload,
    bool Readable,
    IReadOnlyList<string> ReadErrors,
    IReadOnlyList<PayloadChange> Changes,
    string? Note,
    DateTime CreatedUtc,
    string CreatedBy);

public sealed record PostAttempt(
    long Id,
    long SavedRequestId,
    int VersionNumber,
    AttemptOutcome Outcome,
    int? HttpStatus,
    string? PostId,
    JsonNode? Result,
    JsonNode? Counts,
    string? Error,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    string StartedBy,
    string? ResolvedBy,
    DateTime? ResolvedUtc,
    string? ResolutionNote);

public sealed record SavedRequestEvent(
    long Id, long SavedRequestId, string Kind, JsonNode? Detail, DateTime AtUtc, string Actor);
