using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1106: the audit feed contract on a real PostgreSQL 16, through the migrations, so the view is the migrated one.
/// In <c>QBAUTOPOST_TEST_AS_APP_ROLE=1</c> mode it runs as <c>qbautopost_app</c>, which proves the view's grant.
/// </summary>
[RequiresPostgres]
public sealed class EfAuditEventsStoreTests(PostgresFixture postgres)
    : AuditEventsStoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider? _services;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        var connectionString = _connectionString = await postgres.CreateDatabaseAsync();
        _services = new ServiceCollection().AddQbAutopostData(connectionString, commandTimeoutSeconds: 30).BuildServiceProvider();
        await PostgresFixture.MigrateAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_connectionString is not null)
        {
            Api.TestDatabase.Release(_connectionString);
        }
    }

    protected override ISavedRequestStore CreateStore() =>
        _services?.GetRequiredService<ISavedRequestStore>() ?? throw new InvalidOperationException(postgres.SkipReason);

    // As UserAuditWriter does: straight through the DbContext. The subject must be a user (foreign key), so it is
    // created on first use.
    protected override async Task<UserAuditEntry> AddUserAuditAsync(ISavedRequestStore store, UserAuditEntry entry)
    {
        var factory = _services!.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (entry.UserId is { } userId && !await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new AppUser
            {
                Id = userId,
                UserName = userId,
                NormalizedUserName = userId.ToUpperInvariant(),
                DisplayName = userId,
                CreatedUtc = entry.AtUtc,
                CreatedBy = "system",
            });
        }

        var row = new UserAuditEntity
        {
            UserId = entry.UserId,
            Actor = entry.Actor,
            Kind = entry.Kind,
            Detail = entry.Detail?.ToJsonString(),
            AtUtc = entry.AtUtc,
        };
        db.UserAudit.Add(row);
        await db.SaveChangesAsync();
        return entry with { Id = row.Id };
    }
}
