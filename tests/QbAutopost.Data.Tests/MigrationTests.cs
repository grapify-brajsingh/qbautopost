using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace QbAutopost.Data.Tests;

public sealed class MigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // Never opened: the model and the migrations are compared in memory.
    private const string DummyConnection = "Host=127.0.0.1;Database=qbautopost_model_only";

    // Port 1 refuses at once; the short timeout bounds the test if a firewall drops the packets instead.
    private const string RefusedConnection = "Host=127.0.0.1;Port=1;Database=qbautopost;Username=nobody;Timeout=3";

    [Fact]
    public void Should_HaveNoPendingModelChanges()
    {
        using var db = CreateContext(DummyConnection);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Should_ReportUnreachable_When_ConnectionRefused()
    {
        await using var db = CreateContext(RefusedConnection);

        var status = await SchemaState.CheckAsync(db, CancellationToken.None);

        Assert.False(status.Reachable);
        Assert.False(status.Current);
        Assert.Equal("unreachable", status.Reason);
    }

    [PostgresFact]
    public async Task Should_ApplyAllMigrations_When_DatabaseIsEmpty()
    {
        await using var db = CreateContext(await postgres.CreateAdminDatabaseAsync());

        await db.Database.MigrateAsync();
        var status = await SchemaState.CheckAsync(db, CancellationToken.None);

        Assert.True(status.Reachable);
        Assert.True(status.Current);
        Assert.Empty(status.Pending);
        Assert.Null(status.Reason);
    }

    [PostgresFact]
    public async Task Should_ReportOutOfDate_When_LastMigrationNotApplied()
    {
        await using var db = CreateContext(await postgres.CreateAdminDatabaseAsync());
        var migrations = db.Database.GetMigrations().ToList();
        if (migrations.Count > 1)
        {
            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        }

        var status = await SchemaState.CheckAsync(db, CancellationToken.None);

        Assert.True(status.Reachable);
        Assert.False(status.Current);
        Assert.Equal("schema-out-of-date", status.Reason);
        Assert.Equal(migrations[^1], Assert.Single(status.Pending));
    }

    private static QbAutopostDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<QbAutopostDbContext>().UseNpgsql(connectionString).Options);
}
