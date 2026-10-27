using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace QbAutopost.Data.Tests.Api;

/// <summary>A fresh test database for a real host: migrated as deploy migrates a server (spec 5.5), released after.</summary>
public static class TestDatabase
{
    /// <summary>
    /// Closes the idle pooled connections to the test database. Each database has its own pool, and the shared
    /// container's <c>max_connections</c> is shared by every test, so a host's pool must not outlive its test.
    /// </summary>
    public static void Release(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    public static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<QbAutopostDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new QbAutopostDbContext(options);
        await db.Database.MigrateAsync();
    }
}
