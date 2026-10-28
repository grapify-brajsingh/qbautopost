using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// Persistence for saved requests (spec §5.2, §9, §10). Implemented by the in-memory fake and by the EF Core store;
/// both must pass <c>SavedRequestStoreContract</c>. Every mutation is atomic and appends exactly the events listed
/// on its method. Nothing here updates or deletes a version or an event.
/// </summary>
public interface ISavedRequestStore
{
    // Dashboard v2 columns (T-1105): create and append keep skip_count from the stored payload; every snapshot write
    // stores the v1 snapshot and the v2 one (the v1 copy when none is given), and needs_attention_v2 and
    // problem_count with it (see SavedRequestColumns.NeedsAttentionV2).

    /// <summary>
    /// Saves version 1 (and its row fingerprints, kept per version for duplicate detection) and the <c>created</c> event (plus <c>flagged-duplicate</c> when the duplicate is flagged).
    /// The same source and idempotency key with the same original body replays the existing record; with a different
    /// body it reports <see cref="CreateKind.KeyReused"/>.
    /// </summary>
    Task<CreateResult> CreateAsync(NewSavedRequest request, CancellationToken ct);

    Task<SavedRequest?> GetAsync(long id, CancellationToken ct);

    Task<PagedResult<SavedRequestSummary>> ListAsync(SavedRequestQuery query, CancellationToken ct);

    Task<SavedRequestStats> StatsAsync(CancellationToken ct);

    /// <summary>Digests (with the latest version's stored row fingerprints) of requests created at or after <paramref name="sinceUtc"/>, never discarded ones, never <paramref name="excludeId"/>.</summary>
    Task<IReadOnlyList<DuplicateDigest>> DuplicateCandidatesAsync(DateTime sinceUtc, long? excludeId, CancellationToken ct);

    Task<IReadOnlyList<SavedRequestVersion>> VersionsAsync(long id, CancellationToken ct);

    Task<SavedRequestVersion?> VersionAsync(long id, int number, CancellationToken ct);

    Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id, CancellationToken ct);

    Task<PostAttempt?> AttemptAsync(long id, long attemptId, CancellationToken ct);

    Task<IReadOnlyList<SavedRequestEvent>> EventsAsync(long id, CancellationToken ct);

    /// <summary>
    /// Appends a version and bumps the row version; writes one <c>edited</c> event. Checked in order: not found,
    /// <see cref="MutationKind.Stale"/> (row version differs), <see cref="MutationKind.WrongStatus"/> (not pending),
    /// <see cref="MutationKind.Unchanged"/> (same canonical hash as the current version: nothing written).
    /// </summary>
    Task<MutationResult> AppendVersionAsync(long id, int expectedRowVersion, NewVersion version, CancellationToken ct);

    /// <summary>
    /// Stores both validation snapshots (v1 in <paramref name="snapshot"/>, v2 in <paramref name="snapshotV2"/>) and
    /// the duplicate flag of the current version; writes one <c>validated</c> event.
    /// Bumps the row version (it is an update, so an ETag held by the dashboard goes stale).
    /// <see cref="MutationKind.WrongStatus"/> unless pending;
    /// <see cref="MutationKind.Stale"/> when the v1 snapshot is for another version than the current one.
    /// </summary>
    Task<MutationResult> SetValidationAsync(
        long id, ValidationSnapshot snapshot, ValidationSnapshot snapshotV2, DuplicateMatch duplicate, string actor,
        DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Moves <paramref name="from"/> to <paramref name="to"/> (discard, restore), bumps the row version, and writes
    /// one event of <paramref name="eventKind"/>. A <c>reason</c> string in <paramref name="detail"/> becomes the
    /// discard reason when <paramref name="to"/> is discarded; moving to pending clears it.
    /// </summary>
    Task<MutationResult> ChangeStatusAsync(
        long id, int? expectedRowVersion, SavedRequestStatus from, SavedRequestStatus to, string actor,
        DateTime nowUtc, string eventKind, JsonNode? detail, CancellationToken ct);

    /// <summary>
    /// The claim (spec §10.2): pending to posting in one atomic step, which bumps the row version, inserts an
    /// <c>in-progress</c> attempt for the current version, and writes a <c>post-started</c> event. When
    /// <paramref name="acknowledgeDuplicate"/> is true, the same step records the actor and time as the duplicate
    /// acknowledgement. Exactly one of two racing callers is <see cref="ClaimKind.Claimed"/>.
    /// </summary>
    Task<ClaimResult> TryClaimAsync(
        long id, int? expectedRowVersion, string actor, bool acknowledgeDuplicate, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Records an attempt's ending and moves the request from posting to <see cref="AttemptCompletion.NextStatus"/>;
    /// copies the post id onto the request when there is one; writes a <c>post-finished</c> event.
    /// Throws <see cref="InvalidOperationException"/> when the attempt is unknown or no longer in progress.
    /// </summary>
    Task CompleteAttemptAsync(long attemptId, AttemptCompletion completion, CancellationToken ct);

    /// <summary>
    /// Admin resolution of a partial or unknown request, allowed by <see cref="SavedRequestRules.CanResolveTo"/>
    /// (otherwise <see cref="MutationKind.WrongStatus"/>). Posted copies the attempt's post id; not-posted returns the
    /// request to pending. Records the resolver, time and note on the latest unknown or partial attempt; writes one
    /// <c>resolved</c> event.
    /// </summary>
    Task<MutationResult> ResolveAsync(
        long id, ResolveOutcome outcome, string note, string actor, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Startup recovery (spec §10.6): every posting request and its in-progress attempt become unknown, with the
    /// error "the service restarted while this post was running". Returns how many requests were moved.
    /// </summary>
    Task<int> RecoverInFlightAsync(DateTime nowUtc, CancellationToken ct);
}
