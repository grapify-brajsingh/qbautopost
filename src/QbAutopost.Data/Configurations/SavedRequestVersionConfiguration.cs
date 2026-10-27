using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>saved_request_versions</c>, append-only (spec §5.2).</summary>
public sealed class SavedRequestVersionConfiguration : IEntityTypeConfiguration<SavedRequestVersionEntity>
{
    public void Configure(EntityTypeBuilder<SavedRequestVersionEntity> builder)
    {
        builder.ToTable("saved_request_versions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.Payload).HasColumnType("jsonb");
        builder.Property(e => e.ReadErrors).HasColumnType("text[]");
        builder.Property(e => e.Changes).HasColumnType("jsonb");

        builder.HasIndex(e => new { e.SavedRequestId, e.Number }).IsUnique();
    }
}
