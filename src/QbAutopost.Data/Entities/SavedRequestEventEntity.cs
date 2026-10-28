namespace QbAutopost.Data.Entities;

/// <summary>A row of the append-only timeline <c>saved_request_events</c> (spec §5.2).</summary>
public sealed class SavedRequestEventEntity
{
    public long Id { get; set; }
    public long SavedRequestId { get; set; }

    /// <summary>One of <see cref="Core.SavedRequests.SavedRequestEvents"/>.</summary>
    public string Kind { get; set; } = "";

    public string? Detail { get; set; }
    public DateTime AtUtc { get; set; }
    public string Actor { get; set; } = "";
}
