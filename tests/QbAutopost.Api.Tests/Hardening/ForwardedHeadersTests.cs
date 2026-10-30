using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Api.Tests.TestSupport;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-1601: behind nginx on the same server every request arrives from 127.0.0.1, so the brute-force brake would count
/// all callers as one address. With <c>Api:TrustForwardedHeaders</c> on, and only when the peer is the loopback proxy,
/// the brake counts the address nginx forwards instead.
/// </summary>
public sealed class ForwardedHeadersTests
{
    private const int Limit = 2;

    [Fact]
    public async Task Should_BlockOnlyTheGuessingClient_When_TrustIsOnAndPeerIsLoopback()
    {
        var (first, second) = await GuessThenSecondClient("127.0.0.1", trust: true);

        Assert.Equal(HttpStatusCode.TooManyRequests, first);
        Assert.Equal(HttpStatusCode.Unauthorized, second);
    }

    [Fact]
    public async Task Should_IgnoreTheHeader_When_TrustIsOff()
    {
        var (_, second) = await GuessThenSecondClient("127.0.0.1", trust: false);

        Assert.Equal(HttpStatusCode.TooManyRequests, second);
    }

    [Fact]
    public async Task Should_IgnoreTheHeader_When_PeerIsNotLoopback()
    {
        var (_, second) = await GuessThenSecondClient("10.9.9.9", trust: true);

        Assert.Equal(HttpStatusCode.TooManyRequests, second);
    }

    /// <summary>
    /// Sends <see cref="Limit"/> + 2 bad keys as 10.0.0.1, then one as 10.0.0.2; returns the last answers of each.
    /// The brake trips on failure <see cref="Limit"/> + 1, which still answers 401, so the guess after it is the 429.
    /// </summary>
    private static async Task<(HttpStatusCode First, HttpStatusCode Second)> GuessThenSecondClient(string peer, bool trust)
    {
        using var factory = new ApiFactory();
        using var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:FailedAuthPerMinute"] = Limit.ToString(),
            ["Api:TrustForwardedHeaders"] = trust ? "true" : "false",
        }).WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new PeerFilter(IPAddress.Parse(peer)))));
        using var client = host.CreateClient();

        var first = HttpStatusCode.OK;
        for (var i = 0; i <= Limit + 1; i++)
        {
            first = await Send(client, "10.0.0.1");
        }

        return (first, await Send(client, "10.0.0.2"));
    }

    private static async Task<HttpStatusCode> Send(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/jobs");
        request.Headers.Add("X-Api-Key", "not-the-key");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>The test host has no socket; this gives the connection the peer address a real proxy would have.</summary>
    private sealed class PeerFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = peer;
                    return nextMiddleware();
                });
                next(app);
            };
    }
}
