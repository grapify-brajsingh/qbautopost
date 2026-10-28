namespace QbAutopost.Core.Extraction;

/// <summary>Storage of extraction records (dashboard-api-m12 6.6); the same object as the saved-request store.</summary>
public interface IUploadStore
{
    Task CreateExtractionAsync(StatementExtraction extraction, CancellationToken ct);

    /// <summary>Deletes unsaved records whose <c>ExpiresUtc</c> is at or before <paramref name="nowUtc"/>; returns how many.</summary>
    Task<int> DeleteExpiredExtractionsAsync(DateTime nowUtc, CancellationToken ct);

    Task<StatementExtraction?> GetExtractionAsync(Guid id, CancellationToken ct);
}
