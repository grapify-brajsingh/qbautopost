using Npgsql;

namespace QbAutopost.Data.Tests;

/// <summary>T-1703 (M17 spec 4.3, 4.4): the MultiCompany migration, the audit view and the grants.</summary>
public sealed class MultiCompanyMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [PostgresFact]
    public async Task Should_CreateCompaniesTableAndNullableCompanyColumns_When_Migrated()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        var companies = await QueryAsync(admin,
            "SELECT column_name || ':' || data_type FROM information_schema.columns WHERE table_name = 'companies' ORDER BY 1");
        foreach (var expected in new[]
        {
            "id:integer", "folder_name:text", "code:text", "display_name:text", "file_path:text", "candidate_files:jsonb",
            "state:text", "access:text", "access_checked_utc:timestamp with time zone", "access_message:text",
            "qb_company_name:text", "qb_legal_name:text", "match_names:jsonb", "is_default:boolean",
            "created_utc:timestamp with time zone", "updated_utc:timestamp with time zone", "row_version:integer",
        })
        {
            Assert.Contains(expected, companies);
        }

        var nullable = await QueryAsync(admin,
            "SELECT table_name || '.' || column_name || ':' || is_nullable FROM information_schema.columns WHERE " +
            "(table_name = 'saved_requests' AND column_name IN ('company_id','company_source','company_match')) OR " +
            "(table_name = 'statement_extractions' AND column_name IN ('company_id','company_match')) OR " +
            "(table_name = 'qb_list_syncs' AND column_name = 'company_id') ORDER BY 1");
        Assert.Equal(
            new[]
            {
                "qb_list_syncs.company_id:YES", "saved_requests.company_id:YES", "saved_requests.company_match:YES",
                "saved_requests.company_source:YES", "statement_extractions.company_id:YES", "statement_extractions.company_match:YES",
            },
            nullable);
    }

    [PostgresFact]
    public async Task Should_RefuseSecondCompanyWithFolderNameInAnotherCase_When_Inserting()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();
        const string insert =
            "INSERT INTO companies (folder_name, display_name, candidate_files, state, access, match_names, is_default, created_utc, updated_utc, row_version) " +
            "VALUES ('{0}', 'x', '[]', 'active', 'unchecked', '[]', false, now(), now(), 1)";
        await QueryAsync(admin, string.Format(insert, "Alpha") + " RETURNING id::text");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => QueryAsync(admin, string.Format(insert, "ALPHA") + " RETURNING id::text"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [PostgresFact]
    public async Task Should_ExposeCompanyIdInAuditView_When_Migrated()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        var columns = await QueryAsync(admin, "SELECT column_name FROM information_schema.columns WHERE table_name = 'audit_events'");

        Assert.Contains("company_id", columns);
    }

    [PostgresFact]
    public async Task Should_ShowCompanyEventsWithCompanyId_When_UserAuditHoldsCompanyKinds()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();
        await QueryAsync(admin,
            "INSERT INTO user_audit (user_id, actor, kind, detail, at_utc) VALUES " +
            "(NULL, 'user-1', 'company-updated', '{\"companyId\": 7}', now()), " +
            "(NULL, 'user-1', 'companies-scanned', '{\"added\": 1}', now()) RETURNING id::text");

        var rows = await QueryAsync(admin,
            "SELECT action || ':' || coalesce(company_id::text, 'null') || ':' || (id LIKE 'c%')::text || ':' || coalesce(request_id::text, 'null') " +
            "FROM audit_events WHERE action IN ('company-updated', 'companies-scanned') ORDER BY action");

        Assert.Equal(new[] { "companies-scanned:null:true:null", "company-updated:7:true:null" }, rows);
    }

    [PostgresTheory]
    [InlineData("SELECT", true)]
    [InlineData("INSERT", true)]
    [InlineData("UPDATE", true)]
    [InlineData("DELETE", false)]
    public async Task Should_GrantCompaniesWithoutDelete_When_GrantsApplied(string privilege, bool expected)
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        var held = await QueryAsync(admin, $"SELECT has_table_privilege('{PostgresFixture.AppRole}', 'companies', '{privilege}')::text");

        Assert.Equal(new[] { expected ? "true" : "false" }, held);
    }

    [PostgresFact]
    public async Task Should_AllowUpdateOfCompanyIdOnly_When_AppRoleUpdatesQbListSyncs()
    {
        var (admin, _) = await postgres.CreateAppRoleDatabaseAsync();

        var companyId = await QueryAsync(admin,
            $"SELECT has_column_privilege('{PostgresFixture.AppRole}', 'qb_list_syncs', 'company_id', 'UPDATE')::text");
        var outcome = await QueryAsync(admin,
            $"SELECT has_column_privilege('{PostgresFixture.AppRole}', 'qb_list_syncs', 'outcome', 'UPDATE')::text");

        Assert.Equal(new[] { "true" }, companyId);
        Assert.Equal(new[] { "false" }, outcome);
    }

    [Fact]
    public void Should_NameCompaniesTableInGrants_When_ReadingGrants()
    {
        var grants = string.Join(
            '\n',
            File.ReadAllLines(PostgresFixture.RepoFile("deploy", "grants.sql"))
                .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.Matches(@"GRANT SELECT, INSERT, UPDATE ON[^;]*\bcompanies\b[^;]*TO qbautopost_app;", grants);
        Assert.Matches(@"GRANT UPDATE \(company_id\) ON qb_list_syncs TO qbautopost_app;", grants);
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
