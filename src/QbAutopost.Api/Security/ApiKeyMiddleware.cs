using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Security;
using Serilog.Context;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-13: identifies the caller from <c>X-Api-Key</c> and checks they hold the scope the route needs. Health
/// liveness and readiness need no key (spec §6).
/// <para>
/// Nothing here is ever logged but the client id and the scope that was missing — never the key (CLAUDE.md rule 6),
/// and never a hint about which part of a wrong key was wrong.
/// </para>
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
{
    /// <summary>The implicit client the one shared key resolves to while <c>Api:AllowLegacyKey</c> is true.</summary>
    public const string LegacyClientId = "legacy-shared";

    /// <summary>Where the resolved caller is put, for the endpoints and (T-912) the audit trail to read.</summary>
    public const string ClientItemKey = "QbAutopost.Client";

    /// <summary>T-912 (api-v1 §8): the log property every line carries once the caller is known.</summary>
    public const string ClientIdProperty = "clientId";

    public async Task InvokeAsync(
        HttpContext context,
        ApiClientResolver clients,
        IClock clock,
        AuthBrake brake,
        IHostEnvironment environment,
        IProblemDetailsService problems,
        ILogger<ApiKeyMiddleware> log)
    {
        var scope = ApiRoutes.ScopeFor(context.Request.Method, context.Request.Path, environment.IsDevelopment());
        if (scope is null)
        {
            // Liveness and readiness carry no key, so there is no key here to guess; applying the brake would only
            // break the owner's monitoring because some other address had been guessing.
            await next(context);
            return;
        }

        var address = context.Connection.RemoteIpAddress;

        // T-911 (FR-A-15): an address that has been guessing is refused before its next guess is even compared.
        if (brake.IsBlocked(address, out var retryAfter))
        {
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            log.LogWarning(
                "Request {Method} {Path} refused: {Address} is blocked for another {RetrySeconds} s after repeated failed authentication",
                context.Request.Method, context.Request.Path.Value, address?.ToString() ?? "(none)", seconds);
            context.Response.Headers.RetryAfter = seconds.ToString();
            await Problem(
                context, problems, StatusCodes.Status429TooManyRequests, "Too many failed authentications",
                $"Too many requests with an unrecognised key came from this address. Retry after {seconds} seconds.");
            return;
        }

        var given = context.Request.Headers[ApiSettings.KeyHeader].ToString();
        var client = clients.Resolve(given, clock.UtcNow) ?? Legacy(given);
        if (client is null)
        {
            brake.RecordFailure(address);
            log.LogWarning(
                "Request {Method} {Path} refused: {Problem} {Header} header",
                context.Request.Method, context.Request.Path.Value, given.Length == 0 ? "missing" : "unknown",
                ApiSettings.KeyHeader);
            await Problem(
                context, problems, StatusCodes.Status401Unauthorized, "Missing or invalid API key",
                $"Send a valid key in the {ApiSettings.KeyHeader} header.");
            return;
        }

        context.Items[ClientItemKey] = client;

        // T-912 (api-v1 §8): from here to the end of the request, every line names the caller — including the two
        // refusals below, which are exactly the lines an operator reads when an integration stops working.
        using var named = LogContext.PushProperty(ClientIdProperty, client.Id);

        // The key belongs to somebody, so whatever happens next — wrong network, missing scope — is not guessing.
        brake.RecordSuccess(address);

        if (!client.AllowsAddress(address))
        {
            log.LogWarning(
                "Client {ClientId} refused on {Method} {Path}: address {Address} is outside its allowed networks",
                client.Id, context.Request.Method, context.Request.Path.Value,
                context.Connection.RemoteIpAddress?.ToString() ?? "(none)");
            await Problem(
                context, problems, StatusCodes.Status403Forbidden, "Caller not allowed from this address",
                "This client may only be used from its configured networks.");
            return;
        }

        if (!client.Allows(scope))
        {
            log.LogWarning(
                "Client {ClientId} refused on {Method} {Path}: scope {Scope} is required",
                client.Id, context.Request.Method, context.Request.Path.Value, scope);
            await Problem(
                context, problems, StatusCodes.Status403Forbidden, "Scope not granted",
                $"This key does not carry the {scope} scope, which {context.Request.Path.Value} requires.");
            return;
        }

        await next(context);
    }

    /// <summary>The caller resolved for this request, or null on the routes that need no key.</summary>
    public static ApiClient? ClientOf(HttpContext context) => context.Items[ClientItemKey] as ApiClient;

    /// <summary>True when the caller holds <paramref name="scope"/>; false on an unauthenticated route.</summary>
    public static bool HasScope(HttpContext context, string scope) => ClientOf(context)?.Allows(scope) == true;

    /// <summary>
    /// FR-A-13 back-compat: the single <c>Api:ApiKey</c> stands in as an all-scopes client while
    /// <c>Api:AllowLegacyKey</c> is true, so the POC package and the deploy scripts keep working for one release.
    /// </summary>
    private ApiClient? Legacy(string given)
    {
        var api = settings.Value.Api;
        return api.AllowLegacyKey && Matches(given, api.ApiKey)
            ? new ApiClient
            {
                Id = LegacyClientId,
                Name = "the shared Api:ApiKey",
                KeyHash = string.Empty,
                KeySalt = string.Empty,
                Scopes = [ApiScopes.Admin],
            }
            : null;
    }

    /// <summary>Compares fixed-length hashes so the comparison time reveals neither content nor length.</summary>
    private static bool Matches(string given, string expected) =>
        expected.Length > 0
        && given.Length > 0
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static async Task Problem(
        HttpContext context, IProblemDetailsService problems, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
