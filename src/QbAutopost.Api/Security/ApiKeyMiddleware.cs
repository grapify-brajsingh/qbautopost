using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Security;

/// <summary><c>X-Api-Key</c> on every route except <c>/health/*</c> (spec §6). The key is never logged.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
{
    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problems)
    {
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            || Matches(context.Request.Headers[ApiSettings.KeyHeader].ToString(), settings.Value.Api.ApiKey))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Missing or invalid API key",
                Detail = $"Send the key in the {ApiSettings.KeyHeader} header.",
            },
        });
    }

    /// <summary>Compares fixed-length hashes so the comparison time reveals neither content nor length.</summary>
    private static bool Matches(string given, string expected) =>
        expected.Length > 0
        && given.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
