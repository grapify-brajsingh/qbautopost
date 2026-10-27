using Microsoft.AspNetCore.Identity;

namespace QbAutopost.Data.Entities;

/// <summary>A dashboard user: the Identity user plus the spec §5.2 extension columns. Disabled, never deleted (§5.3).</summary>
public sealed class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool IsDisabled { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime? LastLoginUtc { get; set; }
}
