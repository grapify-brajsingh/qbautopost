using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Core.Pipeline;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>qb_list_syncs</c> (M14 spec §3).</summary>
public sealed class QbListSyncConfiguration : IEntityTypeConfiguration<QbListSyncEntity>
{
    public void Configure(EntityTypeBuilder<QbListSyncEntity> builder)
    {
        builder.ToTable("qb_list_syncs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd().UseIdentityAlwaysColumn();
        builder.Property(e => e.Trigger).HasColumnType("text").HasConversion<KebabCaseEnumConverter<ListSyncTrigger>>();
        builder.Property(e => e.Outcome).HasColumnType("text").HasConversion<KebabCaseEnumConverter<ListSyncOutcome>>();
        builder.Property(e => e.MissingInRules).HasColumnType("jsonb");
        builder.Property(e => e.Lists).HasColumnType("jsonb");

        // M17 (T-1703): lists and sync history per company.
        builder.HasOne<CompanyEntity>().WithMany().HasForeignKey(e => e.CompanyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.CompanyId, e.Outcome, e.Id }).IsDescending(false, false, true).HasDatabaseName("ix_qb_list_syncs_company_id_outcome_id");

        // "Newest ok row" and "newest row" both walk this index from the top.
        builder.HasIndex(e => new { e.Outcome, e.Id }).IsDescending(false, true).HasDatabaseName("ix_qb_list_syncs_outcome_id");
    }
}
