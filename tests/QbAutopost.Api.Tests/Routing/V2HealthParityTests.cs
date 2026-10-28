using Microsoft.AspNetCore.Http;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Security;
using Serilog.Events;

namespace QbAutopost.Api.Tests.Routing;

/// <summary>
/// T-1109, ruling R8 (dashboard v2 spec 2): rate limiting and request logging treat a v2 route exactly as its v1
/// twin. <c>/health/database</c> is a health route on both surfaces, and the saved-request post is in the posting
/// bucket on both.
/// </summary>
public sealed class V2HealthParityTests
{
    [Fact]
    public void Should_UseHealthBucket_When_V2HealthDatabaseCalled()
    {
        Assert.Equal(RateLimitBucket.Health, RateLimitPolicy.BucketFor("GET", "/api/v1/health/database"));
        Assert.Equal(
            RateLimitPolicy.BucketFor("GET", "/api/v1/health/database"),
            RateLimitPolicy.BucketFor("GET", "/api/v2/health/database"));
    }

    [Fact]
    public void Should_LogAtDebug_When_V2HealthDatabaseCalled()
    {
        Assert.Equal(LogEventLevel.Debug, RequestLog.Level(Ok("/api/v1/health/database"), 1, null));
        Assert.Equal(LogEventLevel.Debug, RequestLog.Level(Ok("/api/v2/health/database"), 1, null));
    }

    [Fact]
    public void Should_UsePostBucket_When_V2SavedRequestPostCalled()
    {
        Assert.Equal(RateLimitBucket.Post, RateLimitPolicy.BucketFor("POST", "/api/v1/saved-requests/1/post"));
        Assert.Equal(
            RateLimitPolicy.BucketFor("POST", "/api/v1/saved-requests/1/post"),
            RateLimitPolicy.BucketFor("POST", "/api/v2/saved-requests/1/post"));
    }

    private static HttpContext Ok(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = path;
        http.Response.StatusCode = StatusCodes.Status200OK;
        return http;
    }
}
