namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>user_audit</c> (spec §5.2): what happened to a user, and who did it.</summary>
public sealed class UserAuditEntity
{
    public long Id { get; set; }

    /// <summary>The subject: the user the entry is about.</summary>
    public string UserId { get; set; } = "";

    public string Actor { get; set; } = "";

    /// <summary><c>created</c>, <c>role-changed</c>, <c>disabled</c>, <c>enabled</c>, <c>password-reset</c>, <c>password-changed</c>, <c>login-failed</c>, <c>locked-out</c>, <c>login</c>.</summary>
    public string Kind { get; set; } = "";

    public string? Detail { get; set; }
    public DateTime AtUtc { get; set; }
}
