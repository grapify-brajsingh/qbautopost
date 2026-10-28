using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>T-1108: the pushId contract on a real PostgreSQL 16, plus the race and the stored hex column.</summary>
[RequiresPostgres]
public sealed class EfPushIdStoreTests(PostgresFixture postgres)
    : PushIdStoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
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

    [PostgresFact]
    public async Task Should_StoreOneRow_When_TwoConcurrentSavesShareAPushId()
    {
        var store = CreateStore();
        var bodies = Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? Body() : Body("OTHER")).ToList();

        var results = await Task.WhenAll(bodies.Select(async body =>
            (Body: body, Result: await Task.Run(() => store.CreateAsync(NewRequest(body, PushA), default)))));

        var created = Assert.Single(results, r => r.Result.Kind == CreateKind.Created);
        Assert.All(results, r => Assert.Equal(created.Result.Record.Id, r.Result.Record.Id));
        Assert.All(
            results.Where(r => r.Result.Kind != CreateKind.Created),
            r => Assert.Equal(r.Body == created.Body ? CreateKind.Replayed : CreateKind.PushIdReused, r.Result.Kind));
        Assert.Equal(1, (await store.StatsAsync(default)).Total);
    }

    [PostgresFact]
    public async Task Should_StorePushIdHexLowercase_When_PushIdSaved()
    {
        var store = CreateStore();
        await store.CreateAsync(NewRequest(Body(), Guid.Parse("6F1C2A4E-9B7D-4C3E-8A21-0D5E6F7A8B9C")), default);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT push_id_hex FROM saved_requests", connection);
        var hex = (string?)await command.ExecuteScalarAsync();

        Assert.Equal("6f1c2a4e9b7d4c3e8a210d5e6f7a8b9c", hex);
        Assert.Matches("^[0-9a-f]{32}$", hex!);
    }
}
