namespace QbAutopost.Data.Entities;

/// <summary>
/// A row of the read-only view <c>audit_events</c> (T-1106, spec saved-requests-dashboard-v2 8.3). Keyless: the view
/// is never written. The jsonb columns are read as their JSON text.
/// </summary>
public sealed class AuditEventRow
{
    public string Id { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public string ActorId { get; set; } = "";
    public string ActorKind { get; set; } = "";
    public string Action { get; set; } = "";
    public long? RequestId { get; set; }
    public int? RowIndex { get; set; }
    public string? RowExternalId { get; set; }
    public string? Field { get; set; }
    public string? Old { get; set; }
    public string? New { get; set; }
    public int? VersionNumber { get; set; }
    public string? Detail { get; set; }
}
