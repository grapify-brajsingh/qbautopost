using Npgsql;

namespace QbAutopost.Data.Tests;

/// <summary>T-1209 (dashboard-api-m12 3.4, 6.6, 7.1): the DashboardM12 migration and its grants.</summary>
public sealed class DashboardM12MigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [PostgresFact]
    public async Task Should_CreateTableColumnsAndIndexes_When_Migrated()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        var columns = await QueryAsync(admin,
            "SELECT table_name || '.' || column_name || ':' || data_type || ':' || is_nullable FROM information_schema.columns " +
            "WHERE table_name = 'statement_extractions' OR (table_name = 'saved_requests' AND column_name LIKE 'upload!_%' ESCAPE '!') ORDER BY 1");
        Assert.Equal(
            new[]
            {
                "saved_requests.upload_extracted_utc:timestamp with time zone:YES",
                "saved_requests.upload_extraction_id:uuid:YES",
                "saved_requests.upload_file_name:text:YES",
                "saved_requests.upload_model:text:YES",
                "saved_requests.upload_prompt_version:text:YES",
                "saved_requests.upload_sha256:text:YES",
                "saved_requests.upload_size_bytes:bigint:YES",
                "statement_extractions.expires_utc:timestamp with time zone:NO",
                "statement_extractions.extracted_utc:timestamp with time zone:NO",
                "statement_extractions.file_name:text:NO",
                "statement_extractions.id:uuid:NO",
                "statement_extractions.model:text:NO",
                "statement_extractions.page_count:integer:NO",
                "statement_extractions.payload:jsonb:NO",
                "statement_extractions.prompt_version:text:NO",
                "statement_extractions.saved_request_id:bigint:YES",
                "statement_extractions.sha256:text:NO",
                "statement_extractions.size_bytes:bigint:NO",
                "statement_extractions.user_id:text:NO",
            },
            columns);

        var indexes = await QueryAsync(admin,
            "SELECT indexname || ' ' || indexdef FROM pg_indexes WHERE indexname IN " +
            "('ix_saved_request_events_kind_at','ix_saved_requests_upload_sha256','ix_statement_extractions_expires') ORDER BY 1");
        Assert.Equal(3, indexes.Count);
        Assert.Contains(indexes, i => i.StartsWith("ix_saved_request_events_kind_at ") && i.Contains("(kind, at_utc DESC)"));
        Assert.Contains(indexes, i => i.StartsWith("ix_saved_requests_upload_sha256 ") && i.Contains("(upload_sha256)"));
        Assert.Contains(
            indexes,
            i => i.StartsWith("ix_statement_extractions_expires ") && i.Contains("(expires_utc)") && i.Contains("saved_request_id IS NULL"));

        var foreignKeys = await QueryAsync(admin,
            "SELECT confrelid::regclass::text FROM pg_constraint WHERE conrelid = 'statement_extractions'::regclass AND contype = 'f'");
        Assert.Equal(new[] { "saved_requests" }, foreignKeys);
    }

    [PostgresTheory]
    [InlineData("statement_extractions", true, true, true, true)]
    [InlineData("saved_request_versions", true, true, false, false)]
    [InlineData("saved_request_events", true, true, false, false)]
    [InlineData("user_audit", true, true, false, false)]
    [InlineData("saved_requests", true, true, true, false)]
    public async Task Should_GrantOnlyExtractionTable_When_GrantsApplied(string table, bool select, bool insert, bool update, bool delete)
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        foreach (var (privilege, expected) in new[] { ("SELECT", select), ("INSERT", insert), ("UPDATE", update), ("DELETE", delete) })
        {
            var held = await QueryAsync(admin, $"SELECT has_table_privilege('{PostgresFixture.AppRole}', '{table}', '{privilege}')::text");
            Assert.Equal(new[] { expected ? "true" : "false" }, held);
        }
    }

    [Fact]
    public void Should_NameExtractionTableWithItsFourRights_When_ReadingGrants()
    {
        var grants = string.Join(
            '\n',
            File.ReadAllLines(PostgresFixture.RepoFile("deploy", "grants.sql"))
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.Matches(@"GRANT SELECT, INSERT, UPDATE, DELETE ON\s+statement_extractions\s+TO qbautopost_app;", grants);
    }

    [PostgresFact]
    public async Task Should_KeepV1Isolation_When_Migrated()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        // The seven upload columns are nullable, so a v1 insert that names none of them still works.
        var required = await QueryAsync(admin,
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'saved_requests' " +
            "AND column_name LIKE 'upload!_%' ESCAPE '!' AND is_nullable = 'NO'");
        Assert.Empty(required);
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
