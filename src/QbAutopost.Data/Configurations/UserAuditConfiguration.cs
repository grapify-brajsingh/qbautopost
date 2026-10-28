using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>user_audit</c> (spec §5.2).</summary>
public sealed class UserAuditConfiguration : IEntityTypeConfiguration<UserAuditEntity>
{
    public void Configure(EntityTypeBuilder<UserAuditEntity> builder)
    {
        builder.ToTable("user_audit");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.Detail).HasColumnType("jsonb");

        // Dashboard v2 audit feed, newest first (T-1105, spec saved-requests-dashboard-v2 4.2).
        builder.HasIndex(e => e.AtUtc).IsDescending();

        // Users are disabled, never deleted (§5.3), so the subject always resolves.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
