namespace QbAutopost.Core.Extraction;

/// <summary>Storage of extraction records (dashboard-api-m12 6.6); the same object as the saved-request store.</summary>
public interface IUploadStore
{
    Task CreateExtractionAsync(StatementExtraction extraction, CancellationToken ct);

    /// <summary>Deletes unsaved records whose <c>ExpiresUtc</c> is at or before <paramref name="nowUtc"/>; returns how many.</summary>
    Task<int> DeleteExpiredExtractionsAsync(DateTime nowUtc, CancellationToken ct);

    Task<StatementExtraction?> GetExtractionAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Saves a reviewed upload in one transaction (dashboard-api-m12 7.2, 7.5), in this order: an idempotency replay
    /// (same key, same body) answers <c>Replayed</c>; the extraction row is locked; unknown or expired answers
    /// <c>Expired</c> (an unsaved expired record is deleted); another user's answers <c>NotFound</c>; an already saved
    /// one answers <c>AlreadySaved</c>; a key held by another body answers <c>KeyReused</c>. Otherwise the request is
    /// inserted with the upload columns copied from the record, version 1 and (when present) version 2 are written with
    /// the same actor and time, the <c>created</c> event carries <c>{ source: "upload", fileName }</c>, and the record's
    /// <c>saved_request_id</c> is set. Every answer but <c>Created</c> and <c>Expired</c> writes nothing.
    /// </summary>
    Task<UploadSaveResult> SaveUploadAsync(NewUploadSave save, CancellationToken ct);

    /// <summary>
    /// Candidates for the upload duplicate check (6.7): every saved request created at or after
    /// <paramref name="sinceUtc"/>, of any status, plus any request whose <c>upload_sha256</c> is
    /// <paramref name="sha256"/> whatever its date; newest <c>created_utc</c> first. Row keys come from the current version.
    /// </summary>
    Task<IReadOnlyList<UploadDuplicateCandidate>> UploadDuplicateCandidatesAsync(string sha256, DateTime sinceUtc, CancellationToken ct);
}
