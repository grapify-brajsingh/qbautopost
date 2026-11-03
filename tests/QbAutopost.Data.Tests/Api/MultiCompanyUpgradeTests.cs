using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>A-9: a database written before M17 is upgraded by the first start of the new host (real PostgreSQL).</summary>
public sealed class MultiCompanyUpgradeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Password = "correct-horse-battery";

    private static readonly string[] OldRowSql =
    [
        """
        insert into saved_requests (status, source, transaction_count, current_version, original_body, original_sha256, canonical_sha256,
            needs_attention, duplicate_reasons, row_fingerprints, created_utc, created_by, updated_utc, updated_by, row_version)
        values ('received', 'api-client:n8n', 1, 1, '{}', repeat('a', 64), repeat('b', 64),
            false, '{}', '{}', now(), 'test', now(), 'test', 1)
        """,
        """
        insert into statement_extractions (id, user_id, file_name, size_bytes, sha256, page_count, model, prompt_version, payload, extracted_utc, expires_utc)
        values (gen_random_uuid(), 'u1', 'a.pdf', 10, 'abc', 1, 'm', 'v', '{}'::jsonb, now(), now() + interval '1 day')
        """,
        """
        insert into qb_list_syncs (trigger, started_utc, finished_utc, outcome)
        values ('schedule', now(), now(), 'ok')
        """,
    ];

    [PostgresFact]
    public async Task Should_AssignEveryOldRowToTheDefaultCompany_When_HostStartsAfterUpgrade()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        await SeedOldRowsAsync(connectionString);
        try
        {
            using var factory = ApiFactory.WithPostgres(connectionString);
            using var client = factory.CreateAuthorizedClient();
            (await client.GetAsync("/api/v1/health/database")).EnsureSuccessStatusCode();

            var defaultId = (await factory.Services.GetRequiredService<ICompanyStore>().ListAsync(default)).Single(c => c.IsDefault).Id;

            Assert.Equal(
                (defaultId, defaultId, defaultId, "migrated"),
                (await ScalarAsync<int>(connectionString, "select company_id from saved_requests"),
                 await ScalarAsync<int>(connectionString, "select company_id from statement_extractions"),
                 await ScalarAsync<int>(connectionString, "select company_id from qb_list_syncs"),
                 await ScalarAsync<string>(connectionString, "select company_source from saved_requests")));
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_LeaveLaterUnassignedRows_When_HostRestarts()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        try
        {
            using (var first = ApiFactory.WithPostgres(connectionString))
            {
                (await first.CreateAuthorizedClient().GetAsync("/api/v1/health/database")).EnsureSuccessStatusCode();
            }

            await SeedOldRowsAsync(connectionString);
            using var second = ApiFactory.WithPostgres(connectionString);
            (await second.CreateAuthorizedClient().GetAsync("/api/v1/health/database")).EnsureSuccessStatusCode();

            Assert.Equal(
                (1L, 1L),
                (await ScalarAsync<long>(connectionString, "select count(*) from companies"),
                 await ScalarAsync<long>(connectionString, "select count(*) from saved_requests where company_id is null")));
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    [PostgresFact]
    public async Task Should_AnswerTheExistingDashboardAsBefore_When_NoCompanyParameterIsSent()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(connectionString);
        await SeedOldRowsAsync(connectionString);
        try
        {
            using var factory = ApiFactory.WithPostgres(connectionString);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsViewerAsync(factory, client));

            using var requests = await client.GetAsync("/api/v2/saved-requests");
            using var lists = await client.GetAsync("/api/v2/quickbooks/lists");

            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (requests.StatusCode, lists.StatusCode));
            // Ruling B11: every field the list had before M17 is still there; new fields are allowed.
            var body = await requests.Content.ReadFromJsonAsync<JsonElement>();
            var item = body.GetProperty("items")[0];
            Assert.All(
                new[] { "id", "publicRef", "status", "rowVersion", "source", "reference", "controlTotal", "transactionCount", "currentVersion", "needsAttention" },
                field => Assert.True(item.TryGetProperty(field, out _), field));
            Assert.Equal(1, body.GetProperty("total").GetInt32());
        }
        finally
        {
            TestDatabase.Release(connectionString);
        }
    }

    /// <summary>One row in each table M17 adds company_id to, company_id left null as before the upgrade.</summary>
    private static async Task SeedOldRowsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var sql in OldRowSql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> SignInAsViewerAsync(ApiFactory factory, HttpClient client)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            Assert.True((await roles.CreateAsync(new IdentityRole(DashboardRoles.Viewer))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = "v@example.com", Email = "v@example.com", DisplayName = "V", CreatedUtc = DateTime.UtcNow, CreatedBy = "test" };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, DashboardRoles.Viewer)).Succeeded);
        }

        using var response = await client.PostAsJsonAsync("/api/v2/auth/login", new { email = "v@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
