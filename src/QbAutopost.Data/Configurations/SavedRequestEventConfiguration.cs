using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>saved_request_events</c>, the append-only timeline (spec §5.2).</summary>
public sealed class SavedRequestEventConfiguration : IEntityTypeConfiguration<SavedRequestEventEntity>
{
    public void Configure(EntityTypeBuilder<SavedRequestEventEntity> builder)
    {
        builder.ToTable("saved_request_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.Detail).HasColumnType("jsonb");
    }
}
