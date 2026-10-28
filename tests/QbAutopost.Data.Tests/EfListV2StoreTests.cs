using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1107: the v2 list, stats and summaries contract on a real PostgreSQL 16, through the migrations (so the DashboardV2
/// columns and the audit view are the migrated ones). Every test gets a fresh, migrated database, as <see cref="EfSavedRequestStoreTests"/> does.
/// </summary>
[RequiresPostgres]
public sealed class EfListV2StoreTests(PostgresFixture postgres)
    : ListV2StoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
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
}
