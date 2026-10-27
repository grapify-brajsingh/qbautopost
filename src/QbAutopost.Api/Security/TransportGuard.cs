using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using QbAutopost.Api.Configuration;

namespace QbAutopost.Api.Security;

/// <summary>
/// FR-A-14: what the app refuses to start with, now that D-4 makes callers remote. Kept free of the host so the rules
/// can be read and tested as rules — a startup guard nobody can exercise is a guard nobody trusts.
/// <para>
/// Each refusal names the setting that would allow it, because the operator reading it at 2am is the one who has to
/// decide whether allowing it is acceptable.
/// </para>
/// </summary>
public static class TransportGuard
{
    /// <summary>The file a <c>PfxPassword</c> is warned about being found in; never its value (CLAUDE.md rule 6).</summary>
    public const string SettingsFileName = "appsettings.json";

    /// <summary>Checks the configuration and throws when it must not be served. Called once, at startup.</summary>
    public static TransportDecision Require(ApiSettings api, PathsSettings paths, bool pfxPasswordFromFile)
    {
        var decision = Inspect(api, paths, pfxPasswordFromFile);
        return decision.Refusal is null
            ? decision
            : throw new InvalidOperationException(decision.Refusal);
    }

    /// <summary>The same rules without the throw, so a test — or a future dry-run switch — can read the verdict.</summary>
    public static TransportDecision Inspect(ApiSettings api, PathsSettings paths, bool pfxPasswordFromFile)
    {
        var warnings = new List<string>();
        var remote = RemoteBinds(api.Bind);
        var insecure = remote.Where(u => !IsHttps(u)).ToList();

        if (api.Cors.AllowedOrigins.Any(o => o.Trim() == "*"))
        {
            // Every route but liveness and readiness needs a key, and a browser will happily attach one for any page
            // a wildcard invites in.
            return Refused(
                "Api:Cors:AllowedOrigins contains '*', which is refused while any route requires an API key. "
                + "Name the origins that may call this server instead.");
        }

        if (insecure.Count > 0 && !api.AllowInsecureRemote)
        {
            return Refused(
                $"Api:Bind listens on {string.Join(", ", insecure)}, which is reachable from other machines, without TLS. "
                + "Set Api:Tls:PfxPath (with Api:Tls:PfxPassword from the environment) or Api:Tls:StoreThumbprint and bind https, "
                + "or set Api:AllowInsecureRemote=true to accept that API keys and amounts travel in the clear.");
        }

        if (remote.Count > 0 && paths.AllowedJobRoots.Count == 0)
        {
            // SPEC-GAP T-911: FR-A-17 requires the allow-list, §12 ships it empty. Empty cannot mean "refuse every
            // job" (no installation would start) and must not silently mean "any absolute path" once strangers can
            // call, so it means unrestricted only while nobody off this machine can reach the port.
            return Refused(
                "Paths:AllowedJobRoots is empty while Api:Bind is reachable from other machines. "
                + "List the folders a caller's job may live in, so a remote caller cannot name any path on this server.");
        }

        if (insecure.Count > 0)
        {
            warnings.Add(
                "Api:AllowInsecureRemote is true: API keys and transaction amounts travel in the clear to and from "
                + $"{string.Join(", ", insecure)}. Configure TLS and turn this off.");
        }

        if (pfxPasswordFromFile && !string.IsNullOrWhiteSpace(api.Tls.PfxPassword))
        {
            warnings.Add(
                $"Api:Tls:PfxPassword was read from {SettingsFileName}. It is accepted, but it belongs in the "
                + "QBAUTOPOST__Api__Tls__PfxPassword environment variable, not in a file that gets copied and backed up.");
        }

        return new TransportDecision(null, warnings);
    }

    /// <summary>FR-A-14: HSTS is worth sending only once the server actually speaks TLS.</summary>
    public static bool UseHsts(ApiSettings api) =>
        api.Tls.Configured || Urls(api.Bind).Any(IsHttps);

    /// <summary>
    /// True when <paramref name="key"/>'s value came from a JSON settings file rather than the environment or the
    /// command line. The last provider wins in <see cref="IConfiguration"/>, so the last one holding the key decides.
    /// </summary>
    public static bool CameFromFile(IConfiguration config, string key)
    {
        if (config is not IConfigurationRoot root)
        {
            return false;
        }

        var fromFile = false;
        foreach (var provider in root.Providers)
        {
            if (provider.TryGet(key, out var value) && !string.IsNullOrEmpty(value))
            {
                fromFile = provider is JsonConfigurationProvider;
            }
        }

        return fromFile;
    }

    /// <summary>The bind URLs that something other than this machine could connect to.</summary>
    private static List<string> RemoteBinds(string bind) =>
        Urls(bind).Where(u => !IsLoopback(u)).ToList();

    private static IEnumerable<string> Urls(string bind) =>
        (bind ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsHttps(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only the spellings of "this machine" are loopback. Everything else — <c>0.0.0.0</c>, <c>*</c>, <c>+</c>, a real
    /// address, a name this code cannot resolve — counts as remote, because an allow-list of safe things is the one
    /// that stays safe when something new turns up (handoff trap 11).
    /// </summary>
    private static bool IsLoopback(string url)
    {
        var host = Host(url);
        return host is "localhost" or "[::1]" or "::1"
               || host.StartsWith("127.", StringComparison.Ordinal);
    }

    private static string Host(string url)
    {
        var scheme = url.IndexOf("//", StringComparison.Ordinal);
        var afterScheme = scheme >= 0 ? url[(scheme + 2)..] : url;
        var end = afterScheme.IndexOfAny(['/', '?', '#']);
        var authority = end >= 0 ? afterScheme[..end] : afterScheme;

        // A bracketed IPv6 host keeps its brackets; only a trailing :port is removed.
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            return close >= 0 ? authority[..(close + 1)] : authority;
        }

        var colon = authority.LastIndexOf(':');
        return colon >= 0 ? authority[..colon] : authority;
    }

    private static TransportDecision Refused(string refusal) => new(refusal, []);
}

/// <summary>
/// The verdict on a configuration: either a <see cref="Refusal"/> that stops startup, or the warnings an operator
/// should see about the way they have chosen to run.
/// </summary>
public sealed record TransportDecision(string? Refusal, IReadOnlyList<string> Warnings);
