namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>statement_extractions</c> (dashboard-api-m12 6.6).</summary>
public sealed class StatementExtractionEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public int PageCount { get; set; }
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    /// <summary>The tidied payload as JSON text, typed <c>jsonb</c>.</summary>
    public string Payload { get; set; } = "{}";
    public DateTime ExtractedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public long? SavedRequestId { get; set; }
}
