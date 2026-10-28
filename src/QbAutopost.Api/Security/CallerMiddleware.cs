using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using QbAutopost.Api.Auth;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.Endpoints;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Security;
using Serilog.Context;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-13 and saved-requests spec 11.3: the caller resolver. It identifies the caller and checks they hold the scope
/// the route needs. Health liveness and readiness, <c>/auth/login</c> and <c>/auth/refresh</c> need no credential.
/// <list type="number">
/// <item><c>Authorization: Bearer</c>: a dashboard user. The JWT is validated, then the user is loaded (enabled, and
/// the security stamp matches; spec 10.11) and gets the scopes of their role.</item>
/// <item>Else <c>X-Api-Key</c>: an API client, exactly as before.</item>
/// <item>Both: <c>400 ambiguous-credentials</c>. Neither: <c>401</c>.</item>
/// </list>
/// <para>
/// Nothing here is ever logged but the caller id and the scope that was missing, never a key or a token (CLAUDE.md
/// rule 6), and never a hint about which part of a wrong credential was wrong.
/// </para>
/// </summary>
public sealed class CallerMiddleware(RequestDelegate next, IOptions<AppSettings> settings)
{
    /// <summary>The implicit client the one shared key resolves to while <c>Api:AllowLegacyKey</c> is true.</summary>
    public const string LegacyClientId = "legacy-shared";

    /// <summary>Where an API-key caller's <see cref="ApiClient"/> is put, for the code that predates users.</summary>
    public const string ClientItemKey = "QbAutopost.Client";

    /// <summary>Where every resolved <see cref="Caller"/> is put, user or key.</summary>
    public const string CallerItemKey = "QbAutopost.Caller";

    /// <summary>T-912 (api-v1 §8): the log property every line carries once the caller is known.</summary>
    public const string ClientIdProperty = "clientId";

    private const string BearerPrefix = "Bearer ";

    private static readonly JsonWebTokenHandler Tokens = new();

    /// <summary>Built on first use: the settings are read once per process, and an unusable key never builds one.</summary>
    private TokenValidationParameters? _validation;

    public async Task InvokeAsync(
        HttpContext context,
        ApiClientResolver clients,
        IClock clock,
        AuthBrake brake,
        IHostEnvironment environment,
        IProblemDetailsService problems,
        ILogger<CallerMiddleware> log)
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
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await Problem(
                context, problems, StatusCodes.Status429TooManyRequests, "Too many failed authentications",
                $"Too many requests with an unrecognised key came from this address. Retry after {seconds} seconds.");
            return;
        }

        var bearer = BearerToken(context.Request);
        var given = context.Request.Headers[ApiSettings.KeyHeader].ToString();
        if (bearer is not null && given.Length > 0)
        {
            log.LogWarning(
                "Request {Method} {Path} refused: both Authorization: Bearer and {Header} were sent",
                context.Request.Method, context.Request.Path.Value, ApiSettings.KeyHeader);
            await Problem(
                context, problems, StatusCodes.Status400BadRequest, "Ambiguous credentials",
                $"Send either Authorization: Bearer or {ApiSettings.KeyHeader}, not both.", "ambiguous-credentials");
            return;
        }

        Caller caller;
        ApiClient? client = null;
        if (bearer is not null)
        {
            if (await UserAsync(context, bearer, brake, address, problems, log) is not { } user)
            {
                return;
            }

            caller = user;
        }
        else
        {
            client = clients.Resolve(given, clock.UtcNow) ?? Legacy(given);
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
            caller = Caller.ForClient(client);
        }

        context.Items[CallerItemKey] = caller;

        // T-912 (api-v1 §8): from here to the end of the request, every line names the caller, including the
        // refusals below, which are exactly the lines an operator reads when an integration stops working.
        using var named = LogContext.PushProperty(ClientIdProperty, LogIdOf(caller));

        // The credential belongs to somebody, so whatever happens next (wrong network, missing scope) is not guessing.
        brake.RecordSuccess(address);

        if (client is not null && !client.AllowsAddress(address))
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

        // Spec 11.2: a temporary password is good for changing itself and nothing else.
        if (caller.MustChangePassword && !ApiRoutes.AllowsPendingPasswordChange(context.Request.Path))
        {
            log.LogWarning(
                "User {ClientId} refused on {Method} {Path}: the password must be changed first",
                LogIdOf(caller), context.Request.Method, context.Request.Path.Value);
            await Problem(
                context, problems, StatusCodes.Status403Forbidden, "Password change required",
                "Change the temporary password (POST /api/v1/auth/change-password) before using anything else.",
                "password-change-required");
            return;
        }

        if (!caller.Allows(scope))
        {
            log.LogWarning(
                "Client {ClientId} refused on {Method} {Path}: scope {Scope} is required",
                LogIdOf(caller), context.Request.Method, context.Request.Path.Value, scope);
            await Problem(
                context, problems, StatusCodes.Status403Forbidden, "Scope not granted",
                caller.Kind == CallerKind.User
                    ? $"Your role does not carry the {scope} scope, which {context.Request.Path.Value} requires."
                    : $"This key does not carry the {scope} scope, which {context.Request.Path.Value} requires.");
            return;
        }

        await next(context);
    }

    /// <summary>The caller resolved for this request, user or key; null on the routes that need no credential.</summary>
    public static Caller? CallerOf(HttpContext context) =>
        context.Items[CallerItemKey] as Caller ?? (ClientOf(context) is { } client ? Caller.ForClient(client) : null);

    /// <summary>The API client resolved for this request; null for a signed-in user and on open routes.</summary>
    public static ApiClient? ClientOf(HttpContext context) => context.Items[ClientItemKey] as ApiClient;

    /// <summary>True when the caller holds <paramref name="scope"/>; false on an unauthenticated route.</summary>
    public static bool HasScope(HttpContext context, string scope) => CallerOf(context)?.Allows(scope) == true;

    /// <summary>What the <c>clientId</c> log property and the audit line carry: the key's id, or <c>user:&lt;id&gt;</c>.</summary>
    public static string LogIdOf(Caller caller) => caller.Kind == CallerKind.User ? "user:" + caller.Id : caller.Id;

    /// <summary>
    /// Spec 11.3 step 1. Writes the refusal and returns null when the token is not good: <c>503</c> when sign-in is
    /// not configured or the database is not ready, <c>401</c> for a bad token or a user who may no longer use it.
    /// </summary>
    private async Task<Caller?> UserAsync(
        HttpContext context, string token, AuthBrake brake, IPAddress? address, IProblemDetailsService problems, ILogger log)
    {
        var jwt = settings.Value.Auth.Jwt;
        if (!jwt.HasUsableKey)
        {
            log.LogWarning(
                "Request {Method} {Path} refused: Bearer sign-in is off because Auth:Jwt:SigningKey is empty or shorter than {MinBytes} bytes",
                context.Request.Method, context.Request.Path.Value, JwtSettings.MinSigningKeyBytes);
            await Problem(
                context, problems, StatusCodes.Status503ServiceUnavailable, "Sign-in not configured",
                "Dashboard sign-in is not configured on this server. API keys are unaffected.", "auth-not-configured");
            return null;
        }

        var parameters = _validation ??= AccessTokens.ValidationParameters(jwt);
        var validation = await Tokens.ValidateTokenAsync(token, parameters);
        if (!validation.IsValid)
        {
            // T-1009: a token that does not validate is a guess as much as an unknown key is. R16: except a genuine one
            // that has only expired, which every open dashboard sends every 15 minutes; counting those would block
            // the office address.
            if (!await IsGenuineButExpiredAsync(token, validation, parameters))
            {
                brake.RecordFailure(address);
            }

            log.LogWarning(
                "Request {Method} {Path} refused: invalid or expired access token ({Reason})",
                context.Request.Method, context.Request.Path.Value, validation.Exception?.GetType().Name ?? "invalid");
            await Unauthorized(context, problems);
            return null;
        }

        // Spec 10.7: users live in the database, so the gate is asked before anything resolves UserManager.
        var gate = context.RequestServices.GetRequiredService<DatabaseGate>();
        if (gate.NeedsLookAgain)
        {
            await gate.RefreshSharedAsync(context.RequestAborted);
        }

        if (!gate.IsReady)
        {
            await DatabaseGateFilter.Unavailable(context, gate).ExecuteAsync(context);
            return null;
        }

        Caller? caller;
        try
        {
            caller = await context.RequestServices.GetRequiredService<IUserGate>()
                .ResolveAsync(new ClaimsPrincipal(validation.ClaimsIdentity), context.RequestAborted);
        }
        catch (Exception ex) when (DatabaseGate.IsDatabaseFailure(ex))
        {
            // Spec 10.7, as DatabaseGateFilter does: the failure is the trigger for the gate to look again.
            log.LogWarning(
                "Request {Method} {Path} refused: the user lookup failed with {ExceptionType}",
                context.Request.Method, context.Request.Path.Value, ex.GetType().Name);
            await gate.RefreshAsync(context.RequestAborted);
            await DatabaseGateFilter.Unavailable(context, gate).ExecuteAsync(context);
            return null;
        }

        if (caller is null)
        {
            // A genuine token for a user who may no longer use it is not guessing, so the brake is not fed: a disabled
            // user's open dashboard must not lock out everyone else behind the same office address.
            log.LogWarning(
                "Request {Method} {Path} refused: the token's user is unknown, disabled, or was signed out everywhere",
                context.Request.Method, context.Request.Path.Value);
            await Unauthorized(context, problems);
            return null;
        }

        return caller;
    }

    /// <summary>
    /// R16: true when the only thing wrong is the lifetime: signature, issuer, audience and <c>typ</c> all validate
    /// once the lifetime is not checked.
    /// </summary>
    private static async Task<bool> IsGenuineButExpiredAsync(
        string token, TokenValidationResult validation, TokenValidationParameters parameters)
    {
        if (validation.Exception is not SecurityTokenExpiredException)
        {
            return false;
        }

        var ignoringLifetime = parameters.Clone();
        ignoringLifetime.ValidateLifetime = false;
        return (await Tokens.ValidateTokenAsync(token, ignoringLifetime)).IsValid;
    }

    /// <summary>The token after <c>Bearer </c>; null when there is no Bearer header. An empty token is still a Bearer attempt.</summary>
    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }

    private static Task Unauthorized(HttpContext context, IProblemDetailsService problems) =>
        Problem(
            context, problems, StatusCodes.Status401Unauthorized, "Invalid or expired access token",
            "Sign in again to get a new access token.", "invalid-token");

    /// <summary>
    /// FR-A-13 back-compat: the single <c>Api:ApiKey</c> stands in as an all-scopes client while
    /// <c>Api:AllowLegacyKey</c> is true, so the POC package and the deploy scripts keep working for one release.
    /// Since T-1009 "all scopes" means all but <see cref="ApiScopes.PeopleOnly"/>, as for any <c>admin</c> key.
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
        HttpContext context, IProblemDetailsService problems, int status, string title, string detail, string? type = null)
    {
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Detail = detail, Type = type },
        });
    }
}
