using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-14: the headers every answer carries, set before anything can write a body — so a refusal gets them as surely
/// as a 200 does, which is the case that matters, since a refusal is what a prodding caller sees most of.
/// <para>
/// <c>Api:AllowInsecureRemote</c> also warns here, once per request. Startup already said it, but a startup line
/// scrolls away in a week and this arrangement should stay uncomfortable for as long as it lasts.
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
{
    public async Task InvokeAsync(HttpContext context, ILogger<SecurityHeadersMiddleware> log)
    {
        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";

            // Kestrel is told not to send this (Program.cs); a proxy or a future host might still try.
            response.Headers.Remove("Server");
            return Task.CompletedTask;
        }, context.Response);

        if (settings.Value.Api.AllowInsecureRemote && !context.Request.IsHttps)
        {
            log.LogWarning(
                "Request {Method} {Path} was served without TLS because Api:AllowInsecureRemote is true; the API key travelled in the clear",
                context.Request.Method, context.Request.Path.Value);
        }

        await next(context);
    }
}
