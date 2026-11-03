using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1115 (dashboard v2 spec 12): <c>GET /api/v2/users</c> takes the repeatable <c>status</c> filter and its rows carry
/// <c>status</c>; <c>POST /api/v2/users</c> answers a v2 <c>Location</c>. On the real host against a real PostgreSQL.
/// One user per status; the disabled one is also locked out and must change its password, and the locked one must
/// change its password, so the precedence disabled &gt; locked &gt; must-change-password &gt; active is exercised.
/// </summary>
public sealed class V2UsersTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string Users = "/api/v2/users";

    private static readonly Dictionary<string, string> StatusByEmail = new()
    {
        ["admin@example.com"] = "active",
        ["must@example.com"] = "must-change-password",
        ["locked@example.com"] = "locked",
        ["off@example.com"] = "disabled",
    };

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
    public async Task Should_FilterByStatus_When_Given()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        foreach (var (email, status) in StatusByEmail)
        {
            Assert.Equal([email], await EmailsAsync(client, token, $"?status={status}"));
        }
    }

    [PostgresFact]
    public async Task Should_OrStatuses_When_Repeated()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        var emails = await EmailsAsync(client, token, "?status=locked&status=disabled");

        Assert.Equal(["locked@example.com", "off@example.com"], emails);
    }

    [PostgresFact]
    public async Task Should_AndStatusWithOtherFilters_When_Combined()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        var emails = await EmailsAsync(client, token, "?status=active&status=locked&role=editor&q=example");

        Assert.Equal(["locked@example.com"], emails);
    }

    [PostgresFact]
    public async Task Should_Answer400_When_StatusUnknown()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        using var response = await Send(client, HttpMethod.Get, Users + "?status=active&status=frozen", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", (await response.ReadProblemAsync()).GetProperty("type").GetString());
    }

    [PostgresFact]
    public async Task Should_ReturnStatus_When_Listed()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        using var response = await Send(client, HttpMethod.Get, Users, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(StatusByEmail.Count, items.Count);
        Assert.All(items, item => Assert.Equal(
            StatusByEmail[item.GetProperty("email").GetString()!], item.GetProperty("status").GetString()));
        var locked = items.Single(i => i.GetProperty("email").GetString() == "locked@example.com");
        Assert.True(locked.GetProperty("mustChangePassword").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, locked.GetProperty("lockedOutUntilUtc").ValueKind);
    }

    [PostgresFact]
    public async Task Should_IgnoreStatus_When_V1List()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        using var response = await Send(client, HttpMethod.Get, "/api/v1/users?status=frozen", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(StatusByEmail.Count, items.Count);
        Assert.All(items, item => Assert.False(item.TryGetProperty("status", out _)));
    }

    [PostgresFact]
    public async Task Should_PointLocationAtV2_When_V2CreatesUser()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        using var created = await Send(client, HttpMethod.Post, Users, token, new { email = "new@example.com", displayName = "New", role = "viewer" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal($"{Users}/{id}", created.Headers.Location!.ToString());
    }

    [PostgresFact]
    public async Task Should_CarryEtagValueAsRowVersion_When_Listed()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);
        var row = await RowAsync(client, token, "off@example.com");

        using var detail = await Send(client, HttpMethod.Get, $"{Users}/{row.GetProperty("id").GetString()}", token);

        Assert.Equal(detail.Headers.ETag!.Tag, $"\"{row.GetProperty("rowVersion").GetString()}\"");
    }

    [PostgresFact]
    public async Task Should_EnableUser_When_IfMatchIsListRowVersion()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);
        var row = await RowAsync(client, token, "off@example.com");

        using var patched = await PatchFromRowAsync(client, token, row, new { disabled = false });

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.False((await patched.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("disabled").GetBoolean());
    }

    [PostgresFact]
    public async Task Should_Answer412_When_ListRowVersionIsStale()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);
        var row = await RowAsync(client, token, "off@example.com");
        using (var first = await PatchFromRowAsync(client, token, row, new { displayName = "Changed" }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var stale = await PatchFromRowAsync(client, token, row, new { disabled = false });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [PostgresFact]
    public async Task Should_OmitRowVersion_When_V1List()
    {
        using var client = Client();
        var token = await SeedAndSignInAsync(client);

        using var response = await Send(client, HttpMethod.Get, "/api/v1/users", token);

        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        Assert.All(items, item => Assert.False(item.TryGetProperty("rowVersion", out _)));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static async Task<JsonElement> RowAsync(HttpClient client, string token, string email)
    {
        using var response = await Send(client, HttpMethod.Get, Users, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray();
        return items.Single(i => i.GetProperty("email").GetString() == email).Clone();
    }

    /// <summary>A PATCH whose If-Match is the list row's <c>rowVersion</c>, as the dashboard's Users list sends it.</summary>
    private static Task<HttpResponseMessage> PatchFromRowAsync(HttpClient client, string token, JsonElement row, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"{Users}/{row.GetProperty("id").GetString()}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{row.GetProperty("rowVersion").GetString()}\"");
        return client.SendAsync(request);
    }

    private HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>One user per status (see the class summary) and the admin's access token, from a v2 sign-in.</summary>
    private async Task<string> SeedAndSignInAsync(HttpClient client)
    {
        var lockedUntil = DateTimeOffset.UtcNow.AddHours(1);
        await AddUserAsync("admin@example.com", DashboardRoles.Admin, u => { });
        await AddUserAsync("must@example.com", DashboardRoles.Viewer, u => u.MustChangePassword = true);
        await AddUserAsync("locked@example.com", DashboardRoles.Editor, u =>
        {
            u.MustChangePassword = true;
            u.LockoutEnd = lockedUntil;
        });
        await AddUserAsync("off@example.com", DashboardRoles.Editor, u =>
        {
            u.IsDisabled = true;
            u.MustChangePassword = true;
            u.LockoutEnd = lockedUntil;
        });

        using var login = await client.PostAsJsonAsync("/api/v2/auth/login", new { email = "admin@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task AddUserAsync(string email, string role, Action<AppUser> shape)
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
            DisplayName = email,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        shape(user);
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
    }

    private static async Task<List<string>> EmailsAsync(HttpClient client, string token, string query)
    {
        using var response = await Send(client, HttpMethod.Get, Users + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("email").GetString()!).ToList();
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request);
    }
}
