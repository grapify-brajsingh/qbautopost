using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>statement_extractions</c> (dashboard-api-m12 6.6).</summary>
public sealed class StatementExtractionConfiguration : IEntityTypeConfiguration<StatementExtractionEntity>
{
    public void Configure(EntityTypeBuilder<StatementExtractionEntity> builder)
    {
        builder.ToTable("statement_extractions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Payload).HasColumnType("jsonb");

        // Expiry sweeps look only at unsaved records.
        builder.HasIndex(e => e.ExpiresUtc).HasFilter("saved_request_id IS NULL").HasDatabaseName("ix_statement_extractions_expires");

        // A saved record is kept for as long as its request exists (same-file, 409), so no cascade.
        builder.HasOne<SavedRequestEntity>().WithMany().HasForeignKey(e => e.SavedRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}
