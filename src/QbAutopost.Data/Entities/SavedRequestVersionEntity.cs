namespace QbAutopost.Data.Entities;

/// <summary>A row of the append-only <c>saved_request_versions</c> (spec §5.2).</summary>
public sealed class SavedRequestVersionEntity
{
    public long Id { get; set; }
    public long SavedRequestId { get; set; }
    public int Number { get; set; }
    public string Payload { get; set; } = "";
    public bool Readable { get; set; }
    public List<string> ReadErrors { get; set; } = [];

    /// <summary>The §8.3 change list as JSON; <c>[]</c> for version 1.</summary>
    public string Changes { get; set; } = "[]";

    public string? Note { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
}
