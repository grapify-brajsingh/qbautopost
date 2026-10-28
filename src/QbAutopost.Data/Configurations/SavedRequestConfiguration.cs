using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Configurations;

/// <summary><c>saved_requests</c> (spec §5.2). Column names are snake-cased by <see cref="SnakeCaseNames"/>.</summary>
public sealed class SavedRequestConfiguration : IEntityTypeConfiguration<SavedRequestEntity>
{
    // Matches SavedRequest.PublicRef ("SR-{Id:000000}"): at least six digits, never truncated.
    private const string PublicRefSql = "'SR-' || CASE WHEN id < 1000000 THEN lpad(id::text, 6, '0') ELSE id::text END";

    private const string PushIdHexSql = "replace(push_id::text, '-', '')";

    public void Configure(EntityTypeBuilder<SavedRequestEntity> builder)
    {
        builder.ToTable("saved_requests");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityAlwaysColumn();

        builder.Property(e => e.PublicRef).HasColumnType("text").HasComputedColumnSql(PublicRefSql, stored: true);
        builder.Property(e => e.Status).HasColumnType("text").HasConversion<KebabCaseEnumConverter<SavedRequestStatus>>();
        builder.Property(e => e.ControlTotal).HasColumnType("numeric(18,2)");
        builder.Property(e => e.OriginalSha256).HasColumnType("char(64)");
        builder.Property(e => e.CanonicalSha256).HasColumnType("char(64)");
        builder.Property(e => e.DuplicateReasons).HasColumnType("text[]");
        builder.Property(e => e.Validation).HasColumnType("jsonb");
        builder.Property(e => e.RowFingerprints).HasColumnType("text[]");
        builder.Property(e => e.RowVersion).IsConcurrencyToken();

        // Dashboard v2 (T-1105, spec saved-requests-dashboard-v2 4.1 and 4.2).
        builder.Property(e => e.PushIdHex).HasColumnType("text").HasComputedColumnSql(PushIdHexSql, stored: true);
        builder.Property(e => e.MetadataErrors).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
        builder.Property(e => e.SkipCount).HasDefaultValue(0);
        builder.Property(e => e.ValidationV2).HasColumnType("jsonb");
        builder.Property(e => e.NeedsAttentionV2).HasDefaultValue(false);
        builder.Property(e => e.ProblemCount).HasDefaultValue(0);

        builder.HasIndex(e => e.PublicRef).IsUnique();
        builder.HasIndex(e => new { e.Status, e.CreatedUtc }).IsDescending(false, true);
        builder.HasIndex(e => e.Reference);
        builder.HasIndex(e => e.CanonicalSha256);
        builder.HasIndex(e => new { e.Source, e.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        builder.HasIndex(e => e.PushId).IsUnique().HasFilter("push_id IS NOT NULL").HasDatabaseName("ux_saved_requests_push_id");
        builder.HasIndex(e => e.PushIdHex).HasOperators("text_pattern_ops").HasDatabaseName("ix_saved_requests_push_id_hex");

        // History is never deleted (§5.2): deleting a request that has rows below it is refused, not cascaded.
        builder.HasMany(e => e.Versions).WithOne().HasForeignKey(v => v.SavedRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(e => e.Attempts).WithOne().HasForeignKey(a => a.SavedRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(e => e.Events).WithOne().HasForeignKey(v => v.SavedRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}
