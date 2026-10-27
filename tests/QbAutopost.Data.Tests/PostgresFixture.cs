using Npgsql;
using Testcontainers.PostgreSql;

namespace QbAutopost.Data.Tests;

/// <summary>
/// One real PostgreSQL 16 in Docker for the whole test run (spec §14). It is started once, on first use, by
/// <see cref="PostgresFactAttribute"/> during discovery, so a test can be skipped before it runs. Docker not
/// reachable, or a container that will not start (a Windows-only engine, say), is a skip with a reason, never a
/// failure. Testcontainers' resource reaper removes the container when the test process ends.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly Lazy<Shared> Instance = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Why the Docker tests are skipped, or null when PostgreSQL is running.</summary>
    public static string? SharedSkipReason => Instance.Value.SkipReason;

    public bool Available => Instance.Value.Container is not null;

    public string? SkipReason => Instance.Value.SkipReason;

    /// <summary>The container's default database. Tests that change the schema use <see cref="CreateDatabaseAsync"/>.</summary>
    public string ConnectionString =>
        Instance.Value.Container?.GetConnectionString()
        ?? throw new InvalidOperationException(SkipReason);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Creates an empty database with a unique name and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    private static Shared Start()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16").Build();
            // Discovery may run on a synchronization context; start off it so the wait cannot deadlock.
            Task.Run(() => container.StartAsync()).GetAwaiter().GetResult();
            return new Shared(container, null);
        }
        catch (Exception ex)
        {
            return new Shared(null, $"PostgreSQL in Docker is not available: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed record Shared(PostgreSqlContainer? Container, string? SkipReason);
}
