using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Core.Companies;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>companies</c> (M17 spec 4.3).</summary>
public sealed class CompanyConfiguration : IEntityTypeConfiguration<CompanyEntity>
{
    public const string FolderIndex = "ix_companies_folder_name";
    public const string DefaultIndex = "ix_companies_is_default";

    public void Configure(EntityTypeBuilder<CompanyEntity> builder)
    {
        builder.ToTable("companies");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.FolderKey).HasColumnType("text").HasComputedColumnSql("lower(folder_name)", stored: true);
        builder.Property(e => e.State).HasColumnType("text").HasConversion<KebabCaseEnumConverter<CompanyState>>();
        builder.Property(e => e.Access).HasColumnType("text").HasConversion<KebabCaseEnumConverter<CompanyAccess>>();
        builder.Property(e => e.CandidateFiles).HasColumnType("jsonb");
        builder.Property(e => e.MatchNames).HasColumnType("jsonb");
        builder.Property(e => e.RowVersion).IsConcurrencyToken();

        // One company per folder, whatever the case (ruling B10): the unique index is on the lower-cased key.
        builder.HasIndex(e => e.FolderKey).IsUnique().HasFilter("folder_name IS NOT NULL").HasDatabaseName(FolderIndex);
        // Exactly one default company.
        builder.HasIndex(e => e.IsDefault).IsUnique().HasFilter("is_default").HasDatabaseName(DefaultIndex);
    }
}
