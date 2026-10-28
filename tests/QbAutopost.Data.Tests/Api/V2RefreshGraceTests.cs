using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Auth;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1115 (dashboard v2 spec 11): the v2 refresh reuse grace, the v2 cookie path and the <c>refresh-reuse</c> audit,
/// on the real host against a real PostgreSQL; v1 refresh keeps no grace. The caller's address comes from a test-only
/// header (<see cref="AddressHeader"/>), because the test server gives every request none.
/// </summary>
public sealed class V2RefreshGraceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string Cookie = "qbautopost_refresh";
    private const string AddressHeader = "X-Test-Remote-Address";
    private const string Home = "10.0.0.1";
    private const string Elsewhere = "10.0.0.2";

    private ApiFactory? _factory;
    private WebApplicationFactory<Program>? _host;
    private string? _connectionString;

    private WebApplicationFactory<Program> Host => _host ?? throw new InvalidOperationException(postgres.SkipReason);

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync();
        await TestDatabase.MigrateAsync(_connectionString);
        _factory = ApiFactory.WithPostgres(_connectionString);
        _host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, AddressFromHeader>()));
    }

    public Task DisposeAsync()
    {
        _host?.Dispose();
        _factory?.Dispose();
        if (_connectionString is not null)
        {
            TestDatabase.Release(_connectionString);
        }

        return Task.CompletedTask;
    }

    [PostgresFact]
    public async Task Should_IssueSecondSuccessor_When_JustRotatedTokenReusedWithinGrace()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);
        var successor = await RefreshedAsync(client, "v2", first, Home);

        using var grace = await RefreshAsync(client, "v2", first, Home);

        Assert.Equal(HttpStatusCode.OK, grace.StatusCode);
        var second = RefreshTokenOf(grace);
        Assert.NotEqual(successor, second);
        var presented = await TokenAsync(first);
        Assert.Equal((await TokenAsync(second)).Id, presented.GraceSuccessorId);
        Assert.Equal((await TokenAsync(successor)).Id, presented.ReplacedById);
        using var successorRefresh = await RefreshAsync(client, "v2", successor, Home);
        using var secondRefresh = await RefreshAsync(client, "v2", second, Home);
        Assert.Equal(HttpStatusCode.OK, successorRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondRefresh.StatusCode);
    }

    [PostgresFact]
    public async Task Should_RevokeFamily_When_ReusedAfterGrace()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);
        var successor = await RefreshedAsync(client, "v2", first, Home);
        var hash = RefreshTokenService.HashOf(first);
        await DbAsync(db => db.RefreshTokens.Where(t => t.TokenSha256 == hash)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedUtc, DateTime.UtcNow.AddSeconds(-31))));

        using var reuse = await RefreshAsync(client, "v2", first, Home);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        await AssertFamilyRevokedAsync(first);
        using var afterTheft = await RefreshAsync(client, "v2", successor, Home);
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [PostgresFact]
    public async Task Should_RevokeFamily_When_GraceAlreadyUsed()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);
        await RefreshedAsync(client, "v2", first, Home);
        await RefreshedAsync(client, "v2", first, Home);

        using var third = await RefreshAsync(client, "v2", first, Home);

        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);
        await AssertFamilyRevokedAsync(first);
    }

    [PostgresFact]
    public async Task Should_RevokeFamily_When_AddressDiffers()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);
        await RefreshedAsync(client, "v2", first, Home);

        using var reuse = await RefreshAsync(client, "v2", first, Elsewhere);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        await AssertFamilyRevokedAsync(first);
    }

    [PostgresFact]
    public async Task Should_RevokeFamily_When_SuccessorAlreadyRotated()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);
        var successor = await RefreshedAsync(client, "v2", first, Home);
        await RefreshedAsync(client, "v2", successor, Home);

        using var reuse = await RefreshAsync(client, "v2", first, Home);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        await AssertFamilyRevokedAsync(first);
    }

    [PostgresFact]
    public async Task Should_WriteRefreshReuseAudit_When_FamilyRevokedOnV2()
    {
        var user = await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);
        await RefreshedAsync(client, "v2", first, Home);

        using var reuse = await RefreshAsync(client, "v2", first, Elsewhere);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var audit = Assert.Single(await DbAsync(db => db.UserAudit.Where(a => a.Kind == "refresh-reuse").ToListAsync()));
        Assert.Equal(user.Id, audit.UserId);
        Assert.Equal(UserAuditWriter.Anonymous, audit.Actor);
        using var detail = JsonDocument.Parse(audit.Detail!);
        Assert.Equal((await TokenAsync(first)).FamilyId, detail.RootElement.GetProperty("familyId").GetGuid());
        Assert.Single(detail.RootElement.EnumerateObject());
    }

    [PostgresFact]
    public async Task Should_KeepNoGrace_When_V1Refresh()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v1", Home);
        await RefreshedAsync(client, "v1", first, Home);

        using var reuse = await RefreshAsync(client, "v1", first, Home);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        await AssertFamilyRevokedAsync(first);
        Assert.Null((await TokenAsync(first)).GraceSuccessorId);
        Assert.Empty(await DbAsync(db => db.UserAudit.Where(a => a.Kind == "refresh-reuse").ToListAsync()));
    }

    [PostgresFact]
    public async Task Should_SetCookiePathV2_When_V2Login()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();

        using var response = await LoginAsync(client, "v2", Home);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = SetCookieOf(response).ToLowerInvariant();
        Assert.Contains("path=/api/v2/auth", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [PostgresFact]
    public async Task Should_SetCookiePathV2_When_V2Refresh()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();
        var first = await SignInAsync(client, "v2", Home);

        using var response = await RefreshAsync(client, "v2", first, Home);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("path=/api/v2/auth", SetCookieOf(response).ToLowerInvariant());
    }

    [PostgresFact]
    public async Task Should_ClearCookieOnV2Path_When_V2RefreshRefused()
    {
        await AddUserAsync("ann@example.com");
        using var client = Client();

        using var response = await RefreshAsync(client, "v2", "not-a-token", Home);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("path=/api/v2/auth", SetCookieOf(response).ToLowerInvariant());
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>Test-only: sets the connection's remote address from <see cref="AddressHeader"/>.</summary>
    private sealed class AddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, call) =>
            {
                if (context.Request.Headers.TryGetValue(AddressHeader, out var value) && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await call(context);
            });
            next(app);
        };
    }

    private HttpClient Client() => Host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task<AppUser> AddUserAsync(string email)
    {
        using var scope = Host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(DashboardRoles.Editor))
        {
            Assert.True((await roles.CreateAsync(new IdentityRole(DashboardRoles.Editor))).Succeeded);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = "Ann Example",
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, DashboardRoles.Editor)).Succeeded);
        return user;
    }

    private async Task<T> DbAsync<T>(Func<QbAutopostDbContext, Task<T>> query)
    {
        await using var db = await Host.Services.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>().CreateDbContextAsync();
        return await query(db);
    }

    private Task<RefreshTokenEntity> TokenAsync(string raw)
    {
        var hash = RefreshTokenService.HashOf(raw);
        return DbAsync(db => db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenSha256 == hash));
    }

    private async Task AssertFamilyRevokedAsync(string raw)
    {
        var family = (await TokenAsync(raw)).FamilyId;
        var tokens = await DbAsync(db => db.RefreshTokens.AsNoTracking().Where(t => t.FamilyId == family).ToListAsync());
        Assert.All(tokens, t => Assert.NotNull(t.RevokedUtc));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string version, string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{version}/auth/login")
        {
            Content = JsonContent.Create(new { email = "ann@example.com", password = Password }),
        };
        request.Headers.Add(AddressHeader, address);
        return client.SendAsync(request);
    }

    private static async Task<string> SignInAsync(HttpClient client, string version, string address)
    {
        using var response = await LoginAsync(client, version, address);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return RefreshTokenOf(response);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string version, string refreshToken, string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/{version}/auth/refresh") { Content = new StringContent("") };
        request.Headers.Add("Cookie", $"{Cookie}={refreshToken}");
        request.Headers.Add(AddressHeader, address);
        return client.SendAsync(request);
    }

    private static async Task<string> RefreshedAsync(HttpClient client, string version, string refreshToken, string address)
    {
        using var response = await RefreshAsync(client, version, refreshToken, address);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return RefreshTokenOf(response);
    }

    private static string SetCookieOf(HttpResponseMessage response) =>
        Assert.Single(response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(Cookie + "=", StringComparison.Ordinal)).ToList()
            : []);

    private static string RefreshTokenOf(HttpResponseMessage response)
    {
        var header = SetCookieOf(response);
        var value = header[(Cookie.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)];
        Assert.False(string.IsNullOrEmpty(value));
        return value;
    }
}
