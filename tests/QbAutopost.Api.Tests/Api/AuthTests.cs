using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// <c>X-Api-Key</c> on every route except liveness and readiness (api-v1 §2.2, which narrows spec §6 now that
/// callers are remote), and the fail-closed startup check behind it.
/// </summary>
public sealed class AuthTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Should_Return401Problem_When_KeyIsMissing()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await response.ReadProblemAsync()).GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("test-key ")]
    [InlineData("TEST-KEY")]
    public async Task Should_Return401_When_KeyIsWrong(string key)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", key);

        using var response = await client.PostAsync("/jobs", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return200_When_KeyIsRight()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_NotAskForKey_When_RouteIsLiveness()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health");

        // T-910 (api-v1 §2.2): only liveness and readiness are open. The deeper health routes open a QuickBooks
        // session or probe the SDK, so with remote callers (D-4) they now need the health:read scope.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_AskForKey_When_RouteIsADeeperHealthCheck()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/quickbooks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Return404_When_RouteIsUnknownAndKeyIsRight()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/no-such-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Should_RefuseToStart_When_ApiKeyIsEmpty()
    {
        using var factory = new ApiFactory();
        var keyless = factory.WithSetting("Api:ApiKey", "");

        var ex = Assert.ThrowsAny<Exception>(() => keyless.CreateClient());

        Assert.Contains("ApiKey", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseToStart_When_NobodyCanAuthenticateAtAll()
    {
        // T-910 / FR-A-13 fail closed: no enabled client and no usable shared key means every route but liveness
        // would be unreachable. Refusing to start says so once, loudly, instead of answering 401 for ever.
        using var factory = new ApiFactory();
        var shut = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Api:AllowLegacyKey"] = "false",
            })));

        var ex = Assert.ThrowsAny<Exception>(() => shut.CreateClient());

        Assert.Contains("No API caller can authenticate", ex.ToString(), StringComparison.Ordinal);
    }
}
