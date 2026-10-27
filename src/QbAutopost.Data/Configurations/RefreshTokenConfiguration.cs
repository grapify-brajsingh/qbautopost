using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>refresh_tokens</c> (spec §5.2).</summary>
public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshTokenEntity>
{
    public void Configure(EntityTypeBuilder<RefreshTokenEntity> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.TokenSha256).HasColumnType("char(64)");

        // R8 (T-1010): a presented token is found by its hash, and one hash names exactly one row.
        builder.HasIndex(e => e.TokenSha256).IsUnique();

        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
