using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1105: the DashboardV2 backfill (spec saved-requests-dashboard-v2 4.2) on a request saved before the migration:
/// the v2 snapshot and flag copy v1's, problem_count counts the copy, skip_count comes from the current payload.
/// </summary>
public sealed class DashboardV2MigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "20261027200531_AuthRefreshTokenHashAndAnonymousAudit";

    private const string Validation =
        """{"state":"Held","validatedUtc":"2026-10-27T12:00:00Z","versionNumber":2,"counts":null,"validation":[{"index":0,"externalId":"e1","reason":"unknown-account","detail":"x","candidates":null,"postedIn":null}],"errors":["a","b"],"unknownNames":null}""";

    [PostgresFact]
    public async Task Should_BackfillV2ColumnsFromV1_When_MigratingExistingRequest()
    {
        var connectionString = await postgres.CreateAdminDatabaseAsync();
        await using (var db = Context(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        await ExecuteAsync(connectionString, $$"""
            INSERT INTO saved_requests (status, source, transaction_count, current_version, original_body, original_sha256,
              canonical_sha256, needs_attention, duplicate_reasons, validation, row_fingerprints, created_utc, created_by,
              updated_utc, updated_by, row_version)
            VALUES ('pending', 's', 2, 2, '{}', repeat('a', 64), repeat('b', 64), true, '{}', '{{Validation}}'::jsonb, '{}',
              now(), 'u', now(), 'u', 1);
            INSERT INTO saved_request_versions (saved_request_id, number, payload, readable, read_errors, changes, created_utc, created_by)
            VALUES (1, 1, '{"transactions":[{"post":false},{"post":false}]}', true, '{}', '[]', now(), 'u'),
                   (1, 2, '{"transactions":[{"post":false},{"post":"false"},{"post":true},3]}', true, '{}', '[]', now(), 'u');
            """);

        await using (var db = Context(connectionString))
        {
            await db.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT validation_v2 = validation, needs_attention_v2, problem_count, skip_count, metadata_errors, push_id_hex FROM saved_requests",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(3, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Empty(reader.GetFieldValue<string[]>(4));
        Assert.True(reader.IsDBNull(5));
    }

    private static QbAutopostDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<QbAutopostDbContext>().UseNpgsql(connectionString).Options);

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
