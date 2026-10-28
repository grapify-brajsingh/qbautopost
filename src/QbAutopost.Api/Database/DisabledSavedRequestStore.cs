using System.Text.Json.Nodes;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Database;

/// <summary>
/// Registered when <c>Database:ConnectionString</c> is empty. The gate reports <c>not-configured</c> and the
/// <c>RequireDatabase</c> filter answers 503 before any store call, so nothing reaches this; a route that forgot the
/// filter fails loudly rather than pretending to save.
/// </summary>
public sealed class DisabledSavedRequestStore : ISavedRequestStore, ISavedRequestAuditStore, ISavedRequestV2Store
{
    private static InvalidOperationException Off() =>
        new("The saved-requests database is not configured (Database:ConnectionString is empty).");

    public Task<CreateResult> CreateAsync(NewSavedRequest request, CancellationToken ct) => throw Off();

    public Task<SavedRequest?> GetAsync(long id, CancellationToken ct) => throw Off();

    public Task<PagedResult<SavedRequestSummary>> ListAsync(SavedRequestQuery query, CancellationToken ct) => throw Off();

    public Task<SavedRequestStats> StatsAsync(CancellationToken ct) => throw Off();

    public Task<IReadOnlyList<DuplicateDigest>> DuplicateCandidatesAsync(DateTime sinceUtc, long? excludeId, CancellationToken ct) => throw Off();

    public Task<IReadOnlyList<SavedRequestVersion>> VersionsAsync(long id, CancellationToken ct) => throw Off();

    public Task<SavedRequestVersion?> VersionAsync(long id, int number, CancellationToken ct) => throw Off();

    public Task<IReadOnlyList<PostAttempt>> AttemptsAsync(long id, CancellationToken ct) => throw Off();

    public Task<PostAttempt?> AttemptAsync(long id, long attemptId, CancellationToken ct) => throw Off();

    public Task<IReadOnlyList<SavedRequestEvent>> EventsAsync(long id, CancellationToken ct) => throw Off();

    public Task<MutationResult> AppendVersionAsync(long id, int expectedRowVersion, NewVersion version, CancellationToken ct) => throw Off();

    public Task<MutationResult> SetValidationAsync(
        long id, ValidationSnapshot snapshot, ValidationSnapshot snapshotV2, DuplicateMatch duplicate, string actor,
        DateTime nowUtc, CancellationToken ct) => throw Off();

    public Task<MutationResult> ChangeStatusAsync(
        long id, int? expectedRowVersion, SavedRequestStatus from, SavedRequestStatus to, string actor,
        DateTime nowUtc, string eventKind, JsonNode? detail, CancellationToken ct) => throw Off();

    public Task<ClaimResult> TryClaimAsync(
        long id, int? expectedRowVersion, string actor, bool acknowledgeDuplicate, DateTime nowUtc, CancellationToken ct) => throw Off();

    public Task CompleteAttemptAsync(long attemptId, AttemptCompletion completion, CancellationToken ct) => throw Off();

    public Task<MutationResult> ResolveAsync(
        long id, ResolveOutcome outcome, string note, string actor, DateTime nowUtc, CancellationToken ct) => throw Off();

    public Task<int> RecoverInFlightAsync(DateTime nowUtc, CancellationToken ct) => throw Off();

    public Task<PagedResult<AuditEvent>> AuditEventsAsync(AuditQuery query, CancellationToken ct) => throw Off();

    public Task<int> AuditEventCountAsync(AuditQuery query, CancellationToken ct) => throw Off();

    public Task<SavedRequest?> GetV2Async(long id, CancellationToken ct) => throw Off();

    public Task<PagedResult<SavedRequestSummary>> ListV2Async(SavedRequestQueryV2 query, CancellationToken ct) => throw Off();

    public Task<SavedRequestStatsV2> StatsV2Async(CancellationToken ct) => throw Off();

    public Task<IReadOnlyList<OtherPush>> OtherUnpostedPushesAsync(long id, CancellationToken ct) => throw Off();
}
