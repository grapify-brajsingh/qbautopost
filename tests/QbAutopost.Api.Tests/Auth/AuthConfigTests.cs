using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>
/// T-1010 (saved-requests spec 10.7, 11.2): what the auth routes answer without a database (rule 1). Sign-in that is
/// not configured, or a database that is not there, is a 503; <c>/auth/me</c> answers from the resolved caller, so the
/// Bearer path is exercised here with <see cref="FakeUsers"/>. The rest is in <c>QbAutopost.Data.Tests/Api/AuthApiTests</c>.
/// </summary>
public sealed class AuthConfigTests : IDisposable
{
    private const string Login = "/api/v1/auth/login";
    private const string Refresh = "/api/v1/auth/refresh";
    private const string Me = "/api/v1/auth/me";
    private const string Logout = "/api/v1/auth/logout";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(Login)]
    [InlineData(Refresh)]
    public async Task Should_Answer503AuthNotConfigured_When_TheSigningKeyIsMissing(string route)
    {
        var host = _factory.WithSetting("Auth:Jwt:SigningKey", "");
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(route, new { email = "ann@example.com", password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("auth-not-configured", (await response.ReadProblemAsync()).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(Login)]
    [InlineData(Refresh)]
    public async Task Should_Answer503DatabaseUnavailable_When_NoDatabaseIsConfigured(string route)
    {
        using var client = _factory.CreateClient();
        await client.GetAsync("/health");
        _factory.Gate.Set(DatabaseState.NotConfigured);

        using var response = await client.PostAsJsonAsync(route, new { email = "ann@example.com", password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database-unavailable", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task Should_DescribeTheCaller_When_MeIsCalledWithABearerToken()
    {
        var token = _factory.Users.TokenFor(DashboardRoles.Editor);
        using var client = Bearer(token);

        using var response = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_factory.Users.IdOf(token), body.GetProperty("id").GetString());
        Assert.Equal(DashboardRoles.Editor, body.GetProperty("role").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("email").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("displayName").GetString()));
        Assert.False(body.GetProperty("mustChangePassword").GetBoolean());
        var scopes = body.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Equal(DashboardRoles.ScopesOf(DashboardRoles.Editor).Order(StringComparer.Ordinal), scopes);
    }

    [Fact]
    public async Task Should_SayThePasswordMustChange_When_MeIsCalledWithATemporaryPassword()
    {
        using var client = Bearer(_factory.Users.TokenFor(DashboardRoles.Viewer, mustChange: true));

        using var response = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
    }

    [Fact]
    public async Task Should_Answer401_When_MeIsCalledWithoutACredential()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(Me);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_ClearTheCookie_When_LogoutCarriesNoRefreshCookie()
    {
        using var client = Bearer(_factory.Users.TokenFor(DashboardRoles.Viewer));

        using var response = await client.PostAsync(Logout, new StringContent(""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.StartsWith("qbautopost_refresh=;", cookie);
        Assert.Contains("path=/api/v1/auth", cookie);
    }

    [Fact]
    public async Task Should_NotMapAuthRoutes_When_TheFlatLegacySurfaceIsUsed()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/auth/login", new { email = "ann@example.com", password = "correct-horse-battery" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
