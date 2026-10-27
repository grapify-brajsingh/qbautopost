using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-17: the body cap, applied before anything reads the body — before the key check, because refusing a flood
/// should not depend on who is sending it, and before T-909's idempotency hash, which buffers the whole body.
/// <para>
/// Kestrel enforces the same number at the socket (<c>Program.cs</c>), and that is the one that matters in
/// production. This exists because that limit lives in the server rather than the app: under the test host, and
/// behind any host that does not apply it, the cap would otherwise be both untested and absent.
/// </para>
/// </summary>
public sealed class RequestSizeMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
{
    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems, ILogger<RequestSizeMiddleware> log)
    {
        var max = settings.Value.Api.MaxRequestBodyBytes;

        // Tell the server too: a chunked body declares no length, so only the server can stop it mid-flight.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = max;
        }

        if (context.Request.ContentLength > max)
        {
            log.LogWarning(
                "Request {Method} {Path} refused: body of {Bytes} bytes is over the {Max} byte limit",
                context.Request.Method, context.Request.Path.Value, context.Request.ContentLength, max);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await problems.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Title = "Request body too large",
                    Detail = $"The body may be at most {max} bytes (Api:MaxRequestBodyBytes). Send fewer transactions per request.",
                },
            });
            return;
        }

        await next(context);
    }
}
