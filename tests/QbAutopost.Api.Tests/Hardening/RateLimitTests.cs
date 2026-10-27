using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Hardening;

/// <summary>
/// T-911 / FR-A-15: rate limiting. With D-4 the port is reachable by strangers, so "how often may you ask?" needs an
/// answer that is not "as often as you like" — and the answer differs by what the asking costs: posting money is ten
/// a minute, reading is a hundred and twenty, health is six hundred.
/// <para>
/// The buckets and the partition key are tested as functions, because the numbers that matter are the production
/// ones and the test host deliberately runs with the limiter off (handoff trap 14) — over a thousand existing tests
/// hammer these same routes.
/// </para>
/// </summary>
public sealed class RateLimitTests
{
    private const string PostRoute = "/api/v1/quickbooks/transactions";

    [Theory]
    [InlineData("POST", "/api/v1/quickbooks/transactions", RateLimitBucket.Post)]
    [InlineData("POST", "/api/v1/jobs/2026-08/post", RateLimitBucket.Post)]
    [InlineData("POST", "/api/v1/batches/abc/undo", RateLimitBucket.Post)]
    // Queueing a job needs jobs:write, not qb:post, but it ends in money moving, so it draws on the smaller budget.
    [InlineData("POST", "/api/v1/jobs", RateLimitBucket.Post)]
    [InlineData("POST", "/api/v1/quickbooks/transactions/validate", RateLimitBucket.Default)]
    [InlineData("GET", "/api/v1/jobs", RateLimitBucket.Default)]
    [InlineData("GET", "/api/v1/health", RateLimitBucket.Health)]
    [InlineData("GET", "/api/v1/health/ready", RateLimitBucket.Health)]
    [InlineData("GET", "/api/v1/health/quickbooks", RateLimitBucket.Health)]
    [InlineData("GET", "/health", RateLimitBucket.Health)]
    public void Should_ChooseTheBucket_ByWhatTheRequestCosts(string method, string path, RateLimitBucket expected) =>
        Assert.Equal(expected, RateLimitPolicy.BucketFor(method, path));

    [Fact]
    public void Should_UseTheSpecDefaults_When_NoClientOverridesThem()
    {
        var limits = new RateLimitSettings();

        Assert.Equal(10, RateLimitPolicy.PermitsFor(RateLimitBucket.Post, limits, client: null));
        Assert.Equal(120, RateLimitPolicy.PermitsFor(RateLimitBucket.Default, limits, client: null));
        Assert.Equal(600, RateLimitPolicy.PermitsFor(RateLimitBucket.Health, limits, client: null));
    }

    [Fact]
    public void Should_PreferTheClientsOwnLimits_When_ClientsJsonSetsThem()
    {
        var client = Client("acme") with { PostPerMinute = 3, DefaultPerMinute = 7 };
        var limits = new RateLimitSettings();

        Assert.Equal(3, RateLimitPolicy.PermitsFor(RateLimitBucket.Post, limits, client));
        Assert.Equal(7, RateLimitPolicy.PermitsFor(RateLimitBucket.Default, limits, client));

        // Health is per address, not per caller, so a client cannot raise it for itself.
        Assert.Equal(600, RateLimitPolicy.PermitsFor(RateLimitBucket.Health, limits, client));
    }

    [Fact]
    public void Should_PartitionByClient_When_TheCallerIsKnown()
    {
        var acme = Context("10.0.0.1", Client("acme"));
        var acmeElsewhere = Context("10.0.0.2", Client("acme"));
        var other = Context("10.0.0.1", Client("other"));

        // One caller shares one budget wherever they call from; two callers never share one.
        Assert.Equal(
            RateLimitPolicy.PartitionKey(acme, RateLimitBucket.Post),
            RateLimitPolicy.PartitionKey(acmeElsewhere, RateLimitBucket.Post));
        Assert.NotEqual(
            RateLimitPolicy.PartitionKey(acme, RateLimitBucket.Post),
            RateLimitPolicy.PartitionKey(other, RateLimitBucket.Post));
    }

    [Fact]
    public void Should_PartitionByAddress_When_TheRouteNeedsNoKey()
    {
        var one = Context("10.0.0.1", client: null);
        var two = Context("10.0.0.2", client: null);

        Assert.NotEqual(
            RateLimitPolicy.PartitionKey(one, RateLimitBucket.Health),
            RateLimitPolicy.PartitionKey(two, RateLimitBucket.Health));
    }

    [Fact]
    public void Should_KeepBucketsApart_When_OneCallerUsesBoth()
    {
        var context = Context("10.0.0.1", Client("acme"));

        // Spending the posting budget must not stop the same caller reading a job's status.
        Assert.NotEqual(
            RateLimitPolicy.PartitionKey(context, RateLimitBucket.Post),
            RateLimitPolicy.PartitionKey(context, RateLimitBucket.Default));
    }

    [Fact]
    public async Task Should_Answer429WithRetryAfter_When_ThePostingRateIsExceeded()
    {
        using var factory = new ApiFactory();
        using var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:PostPerMinute"] = "2",
        });
        using var client = Authorized(host);

        // The bodies are deliberately junk: the limiter runs before the endpoint, so no QuickBooks call is needed to
        // prove a request was counted (CLAUDE.md rule 1).
        var statuses = new List<HttpStatusCode>();
        TimeSpan? retryAfter = null;
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.PostAsJsonAsync(PostRoute, new { transactions = Array.Empty<object>() });
            statuses.Add(response.StatusCode);
            retryAfter = response.Headers.RetryAfter?.Delta;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
        Assert.NotNull(retryAfter);
    }

    [Fact]
    public async Task Should_NotLimitReads_When_ThePostingBudgetIsSpent()
    {
        using var factory = new ApiFactory();
        using var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:PostPerMinute"] = "1",
            ["Api:RateLimits:DefaultPerMinute"] = "50",
        });
        using var client = Authorized(host);

        for (var i = 0; i < 3; i++)
        {
            using var _ = await client.PostAsJsonAsync(PostRoute, new { transactions = Array.Empty<object>() });
        }

        using var read = await client.GetAsync("/api/v1/jobs");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, read.StatusCode);
    }

    [Fact]
    public async Task Should_Answer429_When_HealthIsPolledTooOften()
    {
        using var factory = new ApiFactory();
        using var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Api:RateLimits:Enabled"] = "true",
            ["Api:RateLimits:HealthPerMinute"] = "2",
        });
        using var client = host.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync("/api/v1/health");
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);
    }

    [Fact]
    public async Task Should_NotLimitAnything_When_TheLimiterIsOff()
    {
        // The test host's own setting, asserted rather than assumed: if this ever flips, a thousand tests start
        // flaking and the reason would be very hard to find.
        using var factory = new ApiFactory();
        using var client = factory.CreateAuthorizedClient();

        for (var i = 0; i < 30; i++)
        {
            using var response = await client.GetAsync("/api/v1/health");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Fact]
    public void Should_ShipEnabled_When_NobodyConfiguresIt() => Assert.True(new RateLimitSettings().Enabled);

    private static HttpClient Authorized(WebApplicationFactory<Program> host)
    {
        // Handoff trap 13: WithSettings returns the base factory, which has no CreateAuthorizedClient.
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private static ApiClient Client(string id) =>
        new() { Id = id, KeyHash = string.Empty, KeySalt = string.Empty, Scopes = [ApiScopes.Admin] };

    private static HttpContext Context(string address, ApiClient? client)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (client is not null)
        {
            context.Items[ApiKeyMiddleware.ClientItemKey] = client;
        }

        return context;
    }
}
