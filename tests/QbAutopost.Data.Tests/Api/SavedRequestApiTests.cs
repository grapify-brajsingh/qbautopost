using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
