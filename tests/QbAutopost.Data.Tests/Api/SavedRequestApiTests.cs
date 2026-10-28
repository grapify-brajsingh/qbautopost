using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.SavedRequests;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1012 (saved-requests spec 5.2, 5.3, 6.1, 6.2): the save and read routes on the real host against a real
/// PostgreSQL, so the EF store, the <c>original_body</c> text column and the user-name lookup are the production ones.
/// </summary>
public sealed class SavedRequestApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string Route = "/api/v1/saved-requests";

    private ApiFactory? _factory;
    private string? _connectionString;

    private ApiFactory Factory => _factory ?? throw new InvalidOperationException(postgres.SkipReason);

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(_connectionString);
        _factory = ApiFactory.WithPostgres(_connectionString);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_connectionString is not null)
        {
            TestDatabase.Release(_connectionString);
        }

        return Task.CompletedTask;
    }

    [PostgresFact]
    public async Task Should_ShowTheEditorsNameAndEmail_When_AnEditorSavedTheRequest()
    {
        var editor = await AddUserAsync("asha@example.com", DashboardRoles.Editor, "Asha K");
        using var client = await SignedInAsync("asha@example.com");

        using var saved = await client.PostAsync(Route, SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-good.json")));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        using var response = await client.GetAsync($"{Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var by = body.GetProperty("created").GetProperty("by");
        Assert.Equal(editor.Id, by.GetProperty("id").GetString());
        Assert.Equal("Asha K", by.GetProperty("name").GetString());
        Assert.Equal("asha@example.com", by.GetProperty("email").GetString());
        Assert.Equal("user", body.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(["edit", "validate", "post", "discard"], body.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()));
    }

    /// <summary>Review Focus 2 on the real column: jsonb would reorder keys and drop the duplicate one; the text column must not.</summary>
    [PostgresFact]
    public async Task Should_ReturnExactBytes_When_GetOriginalFromPostgres()
    {
        await AddUserAsync("asha@example.com", DashboardRoles.Editor, "Asha K");
        using var client = await SignedInAsync("asha@example.com");
        var bytes = SavedRequestTestHost.FixtureBytes("n8n-unicode.json");

        using var saved = await client.PostAsync(Route, SavedRequestTestHost.Json(bytes));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        using var original = await client.GetAsync($"{Route}/SR-{id:000000}/original");

        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(bytes, await original.Content.ReadAsByteArrayAsync());
    }

    /// <summary>R33 on the real jsonb column: U+0000 is refused up front with 400, never a 503 that loses the payload.</summary>
    [PostgresTheory]
    [InlineData("n8n-nul-in-memo.json")]
    [InlineData("n8n-nul-in-key.json")]
    public async Task Should_Answer400PayloadNotStorableAndStayReady_When_ThePayloadContainsUPlus0000(string fixture)
    {
        await AddUserAsync("asha@example.com", DashboardRoles.Editor, "Asha K");
        using var client = await SignedInAsync("asha@example.com");

        using var saved = await client.PostAsync(Route, SavedRequestTestHost.Json(SavedRequestTestHost.Fixture(fixture)));

        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Equal("payload-not-storable", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.True(Factory.Services.GetRequiredService<DatabaseGate>().IsReady);
        using var good = await client.PostAsync(Route, SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-good.json")));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [PostgresFact]
    public async Task Should_Answer400PayloadNotStorable_When_AnEditContainsUPlus0000()
    {
        await AddUserAsync("asha@example.com", DashboardRoles.Editor, "Asha K");
        using var client = await SignedInAsync("asha@example.com");
        using var saved = await client.PostAsync(Route, SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-good.json")));
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        using var edited = await SavedRequestTestHost.PutAsync(
            client, id, SavedRequestTestHost.Fixture("n8n-nul-in-memo.json"), "\"1\"");

        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
        Assert.Equal("payload-not-storable", (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        using var detail = await client.GetAsync($"{Route}/{id}");
        Assert.Equal(1, (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentVersion").GetInt32());
        Assert.True(Factory.Services.GetRequiredService<DatabaseGate>().IsReady);
    }

    /// <summary>R33 on the real numeric(18,2) column: the save succeeds, the column is NULL, the payload keeps the number.</summary>
    [PostgresFact]
    public async Task Should_SaveWithNullControlTotal_When_ControlTotalOverflowsNumeric18And2()
    {
        await AddUserAsync("asha@example.com", DashboardRoles.Editor, "Asha K");
        using var client = await SignedInAsync("asha@example.com");

        using var saved = await client.PostAsync(
            Route, SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-control-total-overflow.json")));

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("needsAttention").GetBoolean());
        using var list = await client.GetAsync(Route);
        var item = Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("controlTotal").ValueKind);
        using var detail = await client.GetAsync($"{Route}/{body.GetProperty("id").GetInt64()}");
        Assert.Equal(
            10000000000000000m,
            (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payload").GetProperty("controlTotal").GetDecimal());
    }

    /// <summary>R33: a real error PostgreSQL raises for one statement is not an outage.</summary>
    [PostgresFact]
    public async Task Should_NotCountAsADatabaseFailure_When_PostgresRefusesOneStatement()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1/0", connection);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());

        Assert.False(refused.IsTransient);
        Assert.False(DatabaseGate.IsDatabaseFailure(new InvalidOperationException("wrapped", refused)));
        Assert.Equal("22012", DatabaseGate.SqlStateOf(refused));
    }

    /// <summary>
    /// R33 through the real filter: text columns cannot hold U+0000 either, so an edit note carrying one is refused by
    /// PostgreSQL. That is a 500 for this request, and the gate stays Ready for everyone else.
    /// </summary>
    [PostgresFact]
    public async Task Should_Answer500AndKeepTheGateReady_When_PostgresRefusesAStatement()
    {
        await AddUserAsync("asha@example.com", DashboardRoles.Editor, "Asha K");
        using var client = await SignedInAsync("asha@example.com");
        using var saved = await client.PostAsync(Route, SavedRequestTestHost.Json(SavedRequestTestHost.Fixture("n8n-good.json")));
        var id = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var payload = SavedRequestTestHost.Fixture("n8n-good.json").Replace("n8n-good-2026-08", "n8n-good-edited", StringComparison.Ordinal);

        using var edited = await SavedRequestTestHost.PutAsync(client, id, payload, "\"1\"", "note with \u0000 in it");

        Assert.Equal(HttpStatusCode.InternalServerError, edited.StatusCode);
        Assert.Equal("database-error", (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString());
        Assert.True(Factory.Services.GetRequiredService<DatabaseGate>().IsReady);
        using var detail = await client.GetAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<AppUser> AddUserAsync(string email, string role, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(role))
        {
            Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }
}
