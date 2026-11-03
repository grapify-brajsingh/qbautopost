using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>Saved requests and dashboard users on one PostgreSQL database (spec saved-requests-postgres §5).</summary>
public sealed class QbAutopostDbContext(DbContextOptions<QbAutopostDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole, string>(options)
{
    public DbSet<SavedRequestEntity> SavedRequests => Set<SavedRequestEntity>();
    public DbSet<SavedRequestVersionEntity> SavedRequestVersions => Set<SavedRequestVersionEntity>();
    public DbSet<PostAttemptEntity> PostAttempts => Set<PostAttemptEntity>();
    public DbSet<SavedRequestEventEntity> SavedRequestEvents => Set<SavedRequestEventEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<UserAuditEntity> UserAudit => Set<UserAuditEntity>();

    public DbSet<StatementExtractionEntity> StatementExtractions => Set<StatementExtractionEntity>();

    public DbSet<QbListSyncEntity> QbListSyncs => Set<QbListSyncEntity>();

    /// <summary>The read-only view <c>audit_events</c> (T-1106).</summary>
    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<IdentityRole>().ToTable("roles");
        builder.Entity<IdentityUserRole<string>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<string>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("role_claims");

        builder.ApplyConfigurationsFromAssembly(typeof(QbAutopostDbContext).Assembly);
        SnakeCaseNames.Apply(builder);
    }
}
