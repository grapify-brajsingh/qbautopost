using QbAutopost.Core.Abstractions;
using Serilog.Context;

namespace QbAutopost.Api.Logging;

/// <summary>
/// api-v1 §2.6: gives every request its <c>requestId</c>, echoes it as <see cref="RequestId.Header"/>, and pushes it
/// onto Serilog's <c>LogContext</c> so every line written while serving the request carries it.
/// <para>
/// It runs <b>first</b>, in front of the exception handler and the request log: an id that only existed for requests
/// that got as far as the key check would be missing from exactly the answers a caller rings up about. The header is
/// set through <c>OnStarting</c> so a refusal written by a middleware that returns early carries it too.
/// </para>
/// </summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IClock clock)
    {
        var inbound = context.Request.Headers.TryGetValue(RequestId.Header, out var header)
            ? header.ToString()
            : null;
        var id = RequestId.Choose(inbound, clock.UtcNow);
        RequestId.Set(context, id);

        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            http.Response.Headers[RequestId.Header] = RequestId.Of(http);
            return Task.CompletedTask;
        }, context);

        using (LogContext.PushProperty(RequestId.PropertyName, id))
        {
            await next(context);
        }
    }
}
