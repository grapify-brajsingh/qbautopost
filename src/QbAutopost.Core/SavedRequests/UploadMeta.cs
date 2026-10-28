namespace QbAutopost.Core.SavedRequests;

/// <summary>
/// The upload columns of a saved request (dashboard-api-m12 7.1, 7.4): copied from the extraction record at save, never
/// from the client, and never changed afterwards. Null on a request that was not saved from an upload.
/// </summary>
public sealed record UploadMeta(
    string FileName, long SizeBytes, string Sha256, string Model, string PromptVersion, DateTime ExtractedUtc);
