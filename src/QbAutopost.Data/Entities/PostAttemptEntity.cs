using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>post_attempts</c> (spec §5.2): one per press of Post that got past the claim.</summary>
public sealed class PostAttemptEntity
{
    public long Id { get; set; }
    public long SavedRequestId { get; set; }
    public int VersionNumber { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public int? HttpStatus { get; set; }
    public string? PostId { get; set; }
    public string? Result { get; set; }
    public string? Counts { get; set; }
    public string? Error { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string StartedBy { get; set; } = "";
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedUtc { get; set; }
    public string? ResolutionNote { get; set; }
}
