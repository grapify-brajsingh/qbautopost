using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace QbAutopost.Api.Tests;

public sealed class HostSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Should_ReturnNotFound_When_RouteIsUnknown()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/no-such-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
