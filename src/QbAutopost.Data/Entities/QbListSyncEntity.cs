using QbAutopost.Core.Pipeline;

namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>qb_list_syncs</c> (M14 spec §3): one list sync attempt, with the lists themselves when it was Ok.</summary>
public sealed class QbListSyncEntity
{
    public long Id { get; set; }
    public ListSyncTrigger Trigger { get; set; }
    public string? RequestedBy { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public ListSyncOutcome Outcome { get; set; }
    public string? Reason { get; set; }
    public string? Error { get; set; }
    public int? Accounts { get; set; }
    public int? Vendors { get; set; }
    public int? Customers { get; set; }

    /// <summary>A JSON string array as text, typed <c>jsonb</c>; Ok rows only.</summary>
    public string? MissingInRules { get; set; }

    /// <summary>The <c>QbLists</c> JSON (names and types only) as text, typed <c>jsonb</c>; Ok rows only.</summary>
    public string? Lists { get; set; }
}
