using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Set to 1 to run the PostgreSQL tests as the least-privileged application role of deploy/grants.sql (T-1016):
    /// <see cref="CreateDatabaseAsync"/> then returns a connection string for <c>qbautopost_app</c>, and
    /// <see cref="MigrateAsync"/> migrates as the superuser and applies the grants. Off by default.
    /// </summary>
    public const string AsAppRoleVariable = "QBAUTOPOST_TEST_AS_APP_ROLE";

    public const string AppRole = "qbautopost_app";

    // A throwaway container's password, never a server's.
    public const string AppRolePassword = "test-only-app-role";

    private static readonly SemaphoreSlim RoleLock = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> AdminByAppString = new();

    private static bool AsAppRole => Environment.GetEnvironmentVariable(AsAppRoleVariable) == "1";

    /// <summary>Creates an empty database with a unique name and returns its connection string (the application role's in that mode).</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var admin = await CreateAdminDatabaseAsync();
        if (!AsAppRole)
        {
            return admin;
        }

        await EnsureAppRoleAsync();
        var app = AppRoleConnectionString(admin);
        AdminByAppString[app] = admin;
        return app;
    }

    /// <summary>Creates an empty database and returns its superuser connection string, whatever the mode.</summary>
    public async Task<string> CreateAdminDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(Unpooled(ConnectionString)))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>The same database as <paramref name="adminConnectionString"/>, as the application role.</summary>
    public static string AppRoleConnectionString(string adminConnectionString) =>
        new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = AppRole, Password = AppRolePassword }.ConnectionString;

    /// <summary>Creates the roles of deploy/install-postgres.ps1 in the cluster, once (roles are cluster-wide).</summary>
    public async Task EnsureAppRoleAsync()
    {
        await RoleLock.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"""
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'qbautopost_owner') THEN CREATE ROLE qbautopost_owner LOGIN PASSWORD '{AppRolePassword}'; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppRole}') THEN CREATE ROLE {AppRole} LOGIN PASSWORD '{AppRolePassword}'; END IF;
                END $$;
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            RoleLock.Release();
        }
    }

    /// <summary>
    /// Migrates a database made by <see cref="CreateDatabaseAsync"/>, as the superuser even in app-role mode, and in
    /// that mode applies deploy/grants.sql, as deploy/apply-migrations.ps1 does on a server.
    /// </summary>
    public static async Task MigrateAsync(string connectionString)
    {
        var admin = AdminByAppString.GetValueOrDefault(connectionString, connectionString);
        await MigrateAsAdminAsync(admin);
        if (admin != connectionString)
        {
            await ApplyGrantsAsync(admin);
        }
    }

    /// <summary>
    /// A migrated database with deploy/grants.sql applied, whatever the mode: the superuser string (to inspect it) and
    /// the <c>qbautopost_app</c> string (to run the code under test with).
    /// </summary>
    public async Task<(string Admin, string App)> CreateAppRoleDatabaseAsync()
    {
        var created = await CreateDatabaseAsync();
        var admin = AdminByAppString.GetValueOrDefault(created, created);
        await EnsureAppRoleAsync();
        await MigrateAsAdminAsync(admin);
        await ApplyGrantsAsync(admin);
        return (admin, AppRoleConnectionString(admin));
    }

    private static async Task MigrateAsAdminAsync(string adminConnectionString)
    {
        var options = new DbContextOptionsBuilder<QbAutopostDbContext>().UseNpgsql(Unpooled(adminConnectionString)).Options;
        await using var db = new QbAutopostDbContext(options);
        await db.Database.MigrateAsync();
    }

    public static async Task ApplyGrantsAsync(string adminConnectionString)
    {
        await using var connection = new NpgsqlConnection(Unpooled(adminConnectionString));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(File.ReadAllText(RepoFile("deploy", "grants.sql")), connection);
        await command.ExecuteNonQueryAsync();
    }

    // Short-lived administrative connections must not sit in a pool: max_connections is shared by every test.
    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    /// <summary>A file under the repository root (found by walking up to QbAutopost.sln).</summary>
    public static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QbAutopost.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException("QbAutopost.sln not found above the test output."), .. parts]);
    }

    private static Shared Start()
    {
        try
        {
            var container = new PostgreSqlBuilder("postgres:16").Build();
            // Discovery may run on a synchronization context; start off it so the wait cannot deadlock. A start that
            // hangs (an image pull that never finishes) is cancelled after two minutes and becomes a skip below.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Task.Run(() => container.StartAsync(timeout.Token)).GetAwaiter().GetResult();
            return new Shared(container, null);
        }
        catch (Exception ex)
        {
            return new Shared(null, $"PostgreSQL in Docker is not available: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed record Shared(PostgreSqlContainer? Container, string? SkipReason);
}
