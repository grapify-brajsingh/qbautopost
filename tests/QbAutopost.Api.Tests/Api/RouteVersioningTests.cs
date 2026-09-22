using System.Net;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-901 (api-v1 §2, §3, §9): every route is served under <c>/api/v1</c>; the flat paths keep working while
/// <c>Api:LegacyRoutes</c> is true and disappear when it is false. Health needs no key under either prefix.
/// </summary>
public sealed class RouteVersioningTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/api/v1/jobs")]
    [InlineData("/api/v1/health/hermes")]
    [InlineData("/api/v1/health/quickbooks")]
    public async Task Should_ServeRoute_When_PathIsUnderApiV1(string path)
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Should_ListJobs_When_PathIsApiV1()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/hermes")]
    [InlineData("/api/v1/health/hermes")]
    public async Task Should_NotRequireApiKey_When_RouteIsHealth(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_Refuse_When_ApiV1RouteIsCalledWithoutApiKey()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_ServeFlatPath_When_LegacyRoutesAreEnabled()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseFlatPath_When_LegacyRoutesAreDisabled()
    {
        using var host = _factory.WithSetting("Api:LegacyRoutes", "false");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var legacy = await client.GetAsync("/jobs");
        using var versioned = await client.GetAsync("/api/v1/jobs");

        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
    }

    [Fact]
    public async Task Should_RefuseFlatHealth_When_LegacyRoutesAreDisabled()
    {
        using var host = _factory.WithSetting("Api:LegacyRoutes", "false");
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/health/hermes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
