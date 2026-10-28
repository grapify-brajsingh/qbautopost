using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>saved_requests</c> (spec §5.2). JSON columns hold serialised text and are typed <c>jsonb</c>.</summary>
public sealed class SavedRequestEntity
{
    public long Id { get; set; }

    /// <summary><c>SR-000123</c>, computed by the database from <see cref="Id"/>.</summary>
    public string PublicRef { get; set; } = "";

    public SavedRequestStatus Status { get; set; }
    public string Source { get; set; } = "";
    public string? Reference { get; set; }
    public decimal? ControlTotal { get; set; }
    public int TransactionCount { get; set; }
    public int CurrentVersion { get; set; }
    public string OriginalBody { get; set; } = "";
    public string OriginalSha256 { get; set; } = "";
    public string CanonicalSha256 { get; set; } = "";
    public string? IdempotencyKey { get; set; }
    public bool NeedsAttention { get; set; }
    public long? DuplicateOfId { get; set; }
    public List<string> DuplicateReasons { get; set; } = [];
    public string? DuplicateAcknowledgedBy { get; set; }
    public DateTime? DuplicateAcknowledgedUtc { get; set; }

    /// <summary>The latest <see cref="ValidationSnapshot"/> as JSON.</summary>
    public string? Validation { get; set; }

    public DateTime? ValidatedUtc { get; set; }
    public string? PostId { get; set; }
    public string? DiscardReason { get; set; }

    /// <summary>The latest version's row fingerprints, for duplicate detection (§9; controller ruling R6).</summary>
    public List<string> RowFingerprints { get; set; } = [];

    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public string UpdatedBy { get; set; } = "";

    // ---- dashboard v2 (T-1105, spec saved-requests-dashboard-v2 4.1) ----

    /// <summary>Email subject of a v2 save, set once at creation.</summary>
    public string? EmailSubject { get; set; }
    public DateTime? EmailReceivedUtc { get; set; }
    public string? EmailFrom { get; set; }
    public string? EmailMessageId { get; set; }
    public Guid? PushId { get; set; }

    /// <summary><see cref="PushId"/> without dashes, computed by the database (prefix search).</summary>
    public string? PushIdHex { get; set; }

    public List<string> MetadataErrors { get; set; } = [];
    public int SkipCount { get; set; }

    /// <summary>The latest v2 <see cref="ValidationSnapshot"/> as JSON.</summary>
    public string? ValidationV2 { get; set; }

    public bool NeedsAttentionV2 { get; set; }
    public int ProblemCount { get; set; }

    /// <summary>Application-managed concurrency token, incremented on every update (§5.1).</summary>
    public int RowVersion { get; set; }

    public List<SavedRequestVersionEntity> Versions { get; set; } = [];
    public List<PostAttemptEntity> Attempts { get; set; } = [];
    public List<SavedRequestEventEntity> Events { get; set; } = [];
}
