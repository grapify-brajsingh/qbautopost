using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>post_attempts</c> (spec §5.2).</summary>
public sealed class PostAttemptConfiguration : IEntityTypeConfiguration<PostAttemptEntity>
{
    public void Configure(EntityTypeBuilder<PostAttemptEntity> builder)
    {
        builder.ToTable("post_attempts");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.Outcome).HasColumnType("text").HasConversion<KebabCaseEnumConverter<AttemptOutcome>>();
        builder.Property(e => e.Result).HasColumnType("jsonb");
        builder.Property(e => e.Counts).HasColumnType("jsonb");
    }
}
