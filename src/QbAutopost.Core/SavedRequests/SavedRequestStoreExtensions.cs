namespace QbAutopost.Core.SavedRequests;

/// <summary>Call shapes kept for callers written before the dashboard v2 columns (T-1105).</summary>
public static class SavedRequestStoreExtensions
{
    /// <summary>
    /// <see cref="ISavedRequestStore.SetValidationAsync"/> with the v1 snapshot also stored as the v2 one: today's
    /// behaviour for a caller that has no v2 judgement.
    /// </summary>
    public static Task<MutationResult> SetValidationAsync(
        this ISavedRequestStore store, long id, ValidationSnapshot snapshot, DuplicateMatch duplicate, string actor,
        DateTime nowUtc, CancellationToken ct) =>
        store.SetValidationAsync(id, snapshot, snapshot, duplicate, actor, nowUtc, ct);
}
