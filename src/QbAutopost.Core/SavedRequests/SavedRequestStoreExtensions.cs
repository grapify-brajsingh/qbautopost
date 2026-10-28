namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// Call shapes kept for the unedited M10 contract tests only (T-1105), which may not be edited (owner ruling O-1). No
/// code under <c>src/</c> calls them; <c>StoreShimUsageTests</c> keeps it that way (T-1112).
/// </summary>
public static class SavedRequestStoreExtensions
{
    /// <summary>
    /// For the unedited M10 contract tests only. <see cref="ISavedRequestStore.SetValidationAsync"/> with the v1
    /// snapshot also stored as the v2 one. Production code computes both snapshots and calls the 7-argument method.
    /// </summary>
    public static Task<MutationResult> SetValidationAsync(
        this ISavedRequestStore store, long id, ValidationSnapshot snapshot, DuplicateMatch duplicate, string actor,
        DateTime nowUtc, CancellationToken ct) =>
        store.SetValidationAsync(id, snapshot, snapshot, duplicate, actor, nowUtc, ct);
}
