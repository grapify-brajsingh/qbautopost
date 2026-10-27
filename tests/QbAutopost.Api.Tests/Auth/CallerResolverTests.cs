using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Auth;

/// <summary>
/// T-1009 (saved-requests spec 10.7, 11.3): what a Bearer request meets before its user is looked up. Sign-in that is
/// not configured, and a database that is not ready, answer 503 while API keys keep working; a token that does not
/// validate feeds the failed-auth brake as an unknown key does.
/// </summary>
public sealed class CallerResolverTests : IDisposable
{
    private const string ListsRoute = "/api/v1/quickbooks/lists";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData("only-31-bytes-long-0123456789ab")]
    public async Task Should_AnswerAuthNotConfigured_When_TheSigningKeyIsShorterThan32Bytes(string key)
    {
        var host = _factory.WithSetting("Auth:Jwt:SigningKey", key);
        using var client = Bearer(host, _factory.Users.TokenFor(DashboardRoles.Viewer));

        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("auth-not-configured", (await response.ReadProblemAsync()).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Should_KeepApiKeysWorking_When_SigningIsNotConfigured()
    {
        var host = _factory.WithSetting("Auth:Jwt:SigningKey", "");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_AnswerDatabaseUnavailableBeforeLookingUpTheUser_When_TheGateIsNotReady()
    {
        using var client = Bearer(_factory, _factory.Users.TokenFor(DashboardRoles.Viewer));
        await client.GetAsync("/health"); // starts the host, whose start-up check makes the gate Ready
        _factory.Gate.Set(DatabaseState.Unreachable);

        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("database-unavailable", (await response.ReadProblemAsync()).GetProperty("type").GetString());
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(0, _factory.Users.Resolutions);
    }

    [Fact]
    public async Task Should_KeepApiKeysWorking_When_TheGateIsNotReady()
    {
        using var client = _factory.CreateAuthorizedClient();
        await client.GetAsync("/health");
        _factory.Gate.Set(DatabaseState.Unreachable);

        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_BlockTheAddress_When_BadBearerTokensReachTheFailedAuthLimit()
    {
        var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:FailedAuthPerMinute"] = "2",
        });
        // The brake blocks on the failure after the limit, as it does for unknown keys.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            using var forged = Bearer(host, _factory.Users.ForgedTokenFor(DashboardRoles.Admin));
            using var refused = await forged.GetAsync(ListsRoute);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var client = Bearer(host, _factory.Users.TokenFor(DashboardRoles.Admin));
        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Should_LetAViewerReadTheLists_When_TheTokenIsGood()
    {
        using var client = Bearer(_factory, _factory.Users.TokenFor(DashboardRoles.Viewer));

        using var response = await client.GetAsync(ListsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
