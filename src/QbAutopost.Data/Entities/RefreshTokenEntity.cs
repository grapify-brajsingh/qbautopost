namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>refresh_tokens</c> (spec §5.2). Only the token's hash is ever stored.</summary>
public sealed class RefreshTokenEntity
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string TokenSha256 { get; set; } = "";
    public Guid FamilyId { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public long? ReplacedById { get; set; }
    public string? CreatedIp { get; set; }
}
