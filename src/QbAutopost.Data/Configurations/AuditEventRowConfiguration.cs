using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary>
/// The view <c>audit_events</c> (T-1106). Created by raw SQL in migration AuditEventsView; a view-mapped entity adds
/// nothing to a migration of its own.
/// </summary>
public sealed class AuditEventRowConfiguration : IEntityTypeConfiguration<AuditEventRow>
{
    public const string ViewName = "audit_events";

    public void Configure(EntityTypeBuilder<AuditEventRow> builder)
    {
        builder.HasNoKey();
        // No table: the DbSet name convention would otherwise give it one, and a migration would create it.
        builder.ToTable((string?)null);
        builder.ToView(ViewName);

        builder.Property(e => e.Old).HasColumnType("jsonb");
        builder.Property(e => e.New).HasColumnType("jsonb");
        builder.Property(e => e.Detail).HasColumnType("jsonb");
    }
}
