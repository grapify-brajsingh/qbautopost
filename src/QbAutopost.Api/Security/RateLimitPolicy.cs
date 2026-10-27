using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Security;

/// <summary>What a request costs, and therefore how often it may be asked for (FR-A-15).</summary>
public enum RateLimitBucket
{
    /// <summary>Liveness, readiness and the deeper probes: polled by monitoring, counted per address.</summary>
    Health,

    /// <summary>Everything else a caller may do with a key.</summary>
    Default,

    /// <summary>The routes that move money, or queue work that will.</summary>
    Post,
}

/// <summary>
/// FR-A-15, on .NET 8's built-in limiter — shared framework, no package (api-v1 §10).
/// <para>
/// Two decisions are worth stating. The bucket comes from <see cref="ApiRoutes.IsIdempotent"/>, the same predicate
/// that names the four mutating routes for T-909, so "what changes something" has one definition instead of two that
/// drift. And the partition is the <i>caller</i>, not the address: a client behind a NAT must not be throttled by its
/// neighbours, and a caller moving between addresses must not win a fresh budget by doing so. Health is the
/// exception and is per address, because it answers without a key at all.
/// </para>
/// </summary>
public static class RateLimitPolicy
{
    /// <summary>The window every bucket counts over. FR-A-15 states every limit per minute.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitBucket BucketFor(string method, PathString path)
    {
        if (ApiRoutes.IsHealth(path))
        {
            return RateLimitBucket.Health;
        }

        // Queueing a job counts as posting: it is slower to reach QuickBooks than a direct post, but it reaches it
        // just the same, and the cheaper bucket is the one that would have to be defended later.
        return ApiRoutes.IsIdempotent(method, path) ? RateLimitBucket.Post : RateLimitBucket.Default;
    }

    public static int PermitsFor(RateLimitBucket bucket, RateLimitSettings limits, ApiClient? client) => bucket switch
    {
        // Per address, so there is no caller whose own setting could raise it.
        RateLimitBucket.Health => limits.HealthPerMinute,
        RateLimitBucket.Post => client?.PostPerMinute ?? limits.PostPerMinute,
        _ => client?.DefaultPerMinute ?? limits.DefaultPerMinute,
    };

    /// <summary>
    /// The budget this request draws on: one per (bucket, caller), or (bucket, address) where there is no caller.
    /// Buckets are kept apart so spending the posting budget still leaves a caller able to read what happened.
    /// </summary>
    public static string PartitionKey(HttpContext context, RateLimitBucket bucket)
    {
        var client = ApiKeyMiddleware.ClientOf(context);
        var who = bucket == RateLimitBucket.Health || client is null
            ? "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "client:" + client.Id;
        return $"{bucket}:{who}";
    }

    /// <summary>Wires the limiter into the host. Called from <c>Program.cs</c>.</summary>
    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var limits = context.RequestServices.GetRequiredService<IOptions<AppSettings>>().Value.Api.RateLimits;
            if (!limits.Enabled)
            {
                return RateLimitPartition.GetNoLimiter("disabled");
            }

            var bucket = BucketFor(context.Request.Method, context.Request.Path);
            var permits = PermitsFor(bucket, limits, ApiKeyMiddleware.ClientOf(context));
            return RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context, bucket), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = Window,
                // Queue 0 (FR-A-15): a caller told to come back is better off than one held on an open socket, and a
                // queue is somewhere a flood can accumulate.
                QueueLimit = 0,
            });
        });

        options.OnRejected = async (context, _) =>
        {
            var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : Window;
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            var http = context.HttpContext;
            http.Response.Headers.RetryAfter = seconds.ToString();

            http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(RateLimitPolicy))
                .LogWarning(
                    "Rate limit reached by {ClientId} on {Method} {Path}; {RetrySeconds} s until it resets",
                    ApiKeyMiddleware.ClientOf(http)?.Id ?? "(no client)",
                    http.Request.Method,
                    http.Request.Path.Value,
                    seconds);

            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                ProblemDetails =
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = $"This caller's limit for {http.Request.Path.Value} has been reached. Retry after {seconds} seconds.",
                },
            });
        };
    }
}
