using System.Net;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>X-Api-Key on every route except /health/* (spec §6).</summary>
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
    public async Task Should_NotAskForKey_When_RouteIsUnderHealth()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/anything");

        // No health endpoints exist until M2/M6; the point is that the key check did not answer 401.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
}
