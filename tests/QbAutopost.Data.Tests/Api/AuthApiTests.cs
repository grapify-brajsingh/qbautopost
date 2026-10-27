using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data.Tests.Api;

/// <summary>
/// T-1010 (saved-requests spec 11.2, 5.2, 10.7, 12): the five auth routes on the real host against a real PostgreSQL,
/// with Identity's real stores (EF InMemory is not an approved package). Every test gets a fresh, migrated database.
/// </summary>
public sealed class AuthApiTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Password = "correct-horse-battery";
    private const string NewPassword = "a-much-longer-passphrase-now";
    private const string Login = "/api/v1/auth/login";
    private const string Refresh = "/api/v1/auth/refresh";
    private const string Logout = "/api/v1/auth/logout";
    private const string Me = "/api/v1/auth/me";
    private const string ChangePassword = "/api/v1/auth/change-password";
    private const string Cookie = "qbautopost_refresh";

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
    public async Task Should_Return200WithTokenAndCookie_When_CredentialsValid()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();

        using var response = await LoginAsync(client, "ANN@example.com", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.True(body.GetProperty("expiresUtc").GetDateTime() > DateTime.UtcNow);
        Assert.Equal(DashboardRoles.Editor, body.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(user.Id, body.GetProperty("user").GetProperty("id").GetString());
        var cookie = SetCookieOf(response).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/v1/auth", cookie);
        Assert.Contains(await AuditKindsAsync(user.Id), k => k == "login");
        Assert.NotNull((await UserAsync(user.Id)).LastLoginUtc);
    }

    [PostgresFact]
    public async Task Should_AnswerMe_When_TheIssuedAccessTokenIsSent()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Viewer);
        using var client = Client();
        var (token, _) = await SignInAsync(client, "ann@example.com", Password);

        using var response = await Send(client, HttpMethod.Get, Me, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, body.GetProperty("id").GetString());
        Assert.Equal("ann@example.com", body.GetProperty("email").GetString());
        Assert.Equal(DashboardRoles.Viewer, body.GetProperty("role").GetString());
        Assert.False(body.GetProperty("mustChangePassword").GetBoolean());
        Assert.Contains(ApiScopes.RequestsRead, body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
    }

    [PostgresFact]
    public async Task Should_Return401SameBody_When_EmailUnknownOrPasswordWrong()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();

        using var unknown = await LoginAsync(client, "nobody@example.com", Password, requestId: "auth-test-0001");
        using var wrong = await LoginAsync(client, "ann@example.com", "wrong-password-here", requestId: "auth-test-0001");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        Assert.Equal(unknownBody, await wrong.Content.ReadAsStringAsync());
        Assert.Contains("invalid-credentials", unknownBody);
        Assert.Contains("login-failed", await AuditKindsAsync(user.Id));
        var anonymous = await DbAsync(db => db.UserAudit.Where(a => a.UserId == null).ToListAsync());
        var failed = Assert.Single(anonymous);
        Assert.Equal("login-failed", failed.Kind);
        Assert.Contains("nobody@example.com", failed.Detail);
    }

    [PostgresFact]
    public async Task Should_Return401_When_UserDisabled()
    {
        await AddUserAsync("ann@example.com", DashboardRoles.Editor, disabled: true);
        using var client = Client();

        using var disabled = await LoginAsync(client, "ann@example.com", Password, requestId: "auth-test-0002");
        using var unknown = await LoginAsync(client, "nobody@example.com", Password, requestId: "auth-test-0002");

        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await disabled.Content.ReadAsStringAsync());
        Assert.Empty(SetCookies(disabled));
    }

    [PostgresFact]
    public async Task Should_Return423WithRetryAfter_When_FiveFailures()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        for (var i = 0; i < 5; i++)
        {
            using var failure = await LoginAsync(client, "ann@example.com", "wrong-password-here");
        }

        using var response = await LoginAsync(client, "ann@example.com", Password);

        Assert.Equal((HttpStatusCode)423, response.StatusCode);
        Assert.Equal("locked-out", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        var retryAfter = int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.InRange(retryAfter, 1, 15 * 60);
        Assert.Contains("locked-out", await AuditKindsAsync(user.Id));
        Assert.Empty(SetCookies(response));
    }

    [PostgresFact]
    public async Task Should_Return429_When_LoginFailuresFromOneAddressPassTheBrake()
    {
        var host = Factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:FailedAuthPerMinute"] = "2",
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        for (var i = 0; i < 3; i++)
        {
            using var failure = await LoginAsync(client, $"guess{i}@example.com", Password);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        using var response = await LoginAsync(client, "guess9@example.com", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [PostgresFact]
    public async Task Should_RotateRefresh_When_CookieValid()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (_, first) = await SignInAsync(client, "ann@example.com", Password);

        using var response = await RefreshAsync(client, first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal(user.Id, body.GetProperty("user").GetProperty("id").GetString());
        var second = RefreshTokenOf(response);
        Assert.NotEqual(first, second);
        var tokens = await DbAsync(db => db.RefreshTokens.Where(t => t.UserId == user.Id).OrderBy(t => t.Id).ToListAsync());
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].RevokedUtc);
        Assert.Equal(tokens[1].Id, tokens[0].ReplacedById);
        Assert.Null(tokens[1].RevokedUtc);
        Assert.Equal(tokens[0].FamilyId, tokens[1].FamilyId);
        Assert.DoesNotContain(tokens, t => t.TokenSha256 == first || t.TokenSha256 == second);
    }

    [PostgresFact]
    public async Task Should_RevokeFamilyAnd401_When_RevokedTokenReused()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (_, first) = await SignInAsync(client, "ann@example.com", Password);
        using var rotated = await RefreshAsync(client, first);
        var second = RefreshTokenOf(rotated);

        using var reuse = await RefreshAsync(client, first);
        using var afterTheft = await RefreshAsync(client, second);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
        var tokens = await DbAsync(db => db.RefreshTokens.Where(t => t.UserId == user.Id).ToListAsync());
        Assert.All(tokens, t => Assert.NotNull(t.RevokedUtc));
    }

    [PostgresFact]
    public async Task Should_RevokeCurrentAndClearCookie_When_Logout()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, refresh) = await SignInAsync(client, "ann@example.com", Password);

        using var response = await Send(client, HttpMethod.Post, Logout, token, refresh);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cleared = SetCookieOf(response).ToLowerInvariant();
        Assert.StartsWith(Cookie + "=;", cleared);
        Assert.Contains("expires=thu, 01 jan 1970", cleared);
        Assert.Contains("path=/api/v1/auth", cleared);
        Assert.NotNull((await DbAsync(db => db.RefreshTokens.SingleAsync(t => t.UserId == user.Id))).RevokedUtc);
        using var refreshed = await RefreshAsync(client, refresh);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [PostgresFact]
    public async Task Should_RevokeOtherTokens_When_PasswordChanged()
    {
        await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, current) = await SignInAsync(client, "ann@example.com", Password);
        var (_, other) = await SignInAsync(client, "ann@example.com", Password);

        using var response = await ChangePasswordAsync(client, token, current, Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var otherRefresh = await RefreshAsync(client, other);
        Assert.Equal(HttpStatusCode.Unauthorized, otherRefresh.StatusCode);
        using var currentRefresh = await RefreshAsync(client, current);
        Assert.Equal(HttpStatusCode.OK, currentRefresh.StatusCode);
        using var withNew = await LoginAsync(client, "ann@example.com", NewPassword);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    [PostgresFact]
    public async Task Should_Return400_When_NewPasswordShorterThan12()
    {
        await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        var (token, current) = await SignInAsync(client, "ann@example.com", Password);

        using var response = await ChangePasswordAsync(client, token, current, Password, "short-11chr");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-password", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        using var withOld = await LoginAsync(client, "ann@example.com", Password);
        Assert.Equal(HttpStatusCode.OK, withOld.StatusCode);
    }

    [PostgresFact]
    public async Task Should_ClearMustChange_When_PasswordChanged()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor, mustChange: true);
        var stampBefore = user.SecurityStamp;
        using var client = Client();
        var (token, current) = await SignInAsync(client, "ann@example.com", Password);

        using var response = await ChangePasswordAsync(client, token, current, Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var after = await UserAsync(user.Id);
        Assert.False(after.MustChangePassword);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
        Assert.Contains("password-changed", await AuditKindsAsync(user.Id));
        using var me = await Send(client, HttpMethod.Get, Me, body.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.False((await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
    }

    [PostgresFact]
    public async Task Should_Answer503_When_DatabaseGateNotReady()
    {
        await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        Factory.Gate.Set(DatabaseState.Unreachable);

        using var response = await LoginAsync(client, "ann@example.com", Password);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database-unavailable", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [PostgresFact]
    public async Task Should_NeverLogPasswordOrToken()
    {
        var user = await AddUserAsync("ann@example.com", DashboardRoles.Editor);
        using var client = Client();
        using var failed = await LoginAsync(client, "ann@example.com", "wrong-password-here");
        var (token, refresh) = await SignInAsync(client, "ann@example.com", Password);
        using var rotated = await RefreshAsync(client, refresh);
        var rotatedRefresh = RefreshTokenOf(rotated);
        var rotatedToken = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        using var changed = await ChangePasswordAsync(client, rotatedToken, rotatedRefresh, Password, NewPassword);
        var changedToken = (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        using var reused = await RefreshAsync(client, refresh);
        using var loggedOut = await Send(client, HttpMethod.Post, Logout, changedToken, rotatedRefresh);
        Assert.Equal(HttpStatusCode.OK, loggedOut.StatusCode);

        var written = string.Join('\n', LogText(), string.Join('\n', await DbAsync(db => db.UserAudit.Select(a => a.Detail).ToListAsync())));

        Assert.Contains(user.Id, written); // the log and the audit were really read
        foreach (var secret in new[] { Password, NewPassword, "wrong-password-here" })
        {
            Assert.DoesNotContain(secret, written);
        }

        foreach (var jwt in new[] { token, rotatedToken, changedToken })
        {
            Assert.All(jwt.Split('.'), part => Assert.DoesNotContain(part, written));
        }

        foreach (var raw in new[] { refresh, rotatedRefresh })
        {
            Assert.DoesNotContain(raw[..16], written);
            Assert.DoesNotContain(raw[^16..], written);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task<AppUser> AddUserAsync(string email, string role, bool disabled = false, bool mustChange = false)
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
            DisplayName = "Ann Example",
            IsDisabled = disabled,
            MustChangePassword = mustChange,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = "test",
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user;
    }

    private async Task<T> DbAsync<T>(Func<QbAutopostDbContext, Task<T>> query)
    {
        await using var db = await Factory.Services.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>().CreateDbContextAsync();
        return await query(db);
    }

    private Task<AppUser> UserAsync(string id) => DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<List<string>> AuditKindsAsync(string userId) =>
        DbAsync(db => db.UserAudit.Where(a => a.UserId == userId).OrderBy(a => a.Id).Select(a => a.Kind).ToListAsync());

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string? requestId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Login) { Content = JsonContent.Create(new { email, password }) };
        if (requestId is not null)
        {
            request.Headers.Add("X-Request-Id", requestId);
        }

        return client.SendAsync(request);
    }

    private static async Task<(string AccessToken, string RefreshToken)> SignInAsync(HttpClient client, string email, string password)
    {
        using var response = await LoginAsync(client, email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("accessToken").GetString()!, RefreshTokenOf(response));
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        Send(client, HttpMethod.Post, Refresh, accessToken: null, refreshToken);

    private static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client, string accessToken, string refreshToken, string currentPassword, string newPassword) =>
        Send(client, HttpMethod.Post, ChangePassword, accessToken, refreshToken, new { currentPassword, newPassword });

    private static Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string path, string? accessToken, string? refreshToken = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (refreshToken is not null)
        {
            request.Headers.Add("Cookie", $"{Cookie}={refreshToken}");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        else if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("");
        }

        return client.SendAsync(request);
    }

    private static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Where(v => v.StartsWith(Cookie + "=", StringComparison.Ordinal)).ToList()
            : [];

    private static string SetCookieOf(HttpResponseMessage response) => Assert.Single(SetCookies(response));

    private static string RefreshTokenOf(HttpResponseMessage response)
    {
        var header = SetCookieOf(response);
        var value = header[(Cookie.Length + 1)..header.IndexOf(';', StringComparison.Ordinal)];
        Assert.False(string.IsNullOrEmpty(value));
        return value;
    }

    /// <summary>Every file the host logs to: the operational log and the FR-A-16 audit trail.</summary>
    private string LogText()
    {
        var folder = Factory.Dir.Combine("data", "logs");
        return string.Join('\n', Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }
}
