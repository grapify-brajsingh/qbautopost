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

        // Dashboard v2 audit feed, newest first (T-1105, spec saved-requests-dashboard-v2 4.2).
        builder.HasIndex(e => e.AtUtc).IsDescending();

        // M12 Home counts of received / posted / post-failed / discarded (dashboard-api-m12 3.4).
        builder.HasIndex(e => new { e.Kind, e.AtUtc }).IsDescending(false, true).HasDatabaseName("ix_saved_request_events_kind_at");
    }
}
