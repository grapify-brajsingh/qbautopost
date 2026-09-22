using QbAutopost.Core.Security;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// Route prefixes (api-v1 §2, §3). Every route is served under <see cref="V1Prefix"/>; the flat paths are mapped as
/// well while <c>Api:LegacyRoutes</c> is true (api-v1 §9), so a caller written against spec §6 keeps working for one
/// release.
/// </summary>
public static class ApiRoutes
{
    public const string V1Prefix = "/api/v1";

    public const string HealthPrefix = "/health";

    /// <summary>
    /// True for a health route under either prefix. Health needs no API key (spec §6) and a healthy poll is logged at
    /// Debug (spec §14), so both places ask here rather than testing one prefix and forgetting the other.
    /// </summary>
    public static bool IsHealth(PathString path) =>
        path.StartsWithSegments(HealthPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(V1Prefix + HealthPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// T-909 (FR-A-12): the four routes that change something and therefore accept an <c>Idempotency-Key</c> — the
    /// direct post, a new job, a job post and an undo. Matched under either prefix.
    /// <para>
    /// Validation and the health and read routes are deliberately absent: replaying a validate would hide a change in
    /// the rules or the ledger, which is the opposite of what a validate is for.
    /// </para>
    /// </summary>
    public static bool IsIdempotent(string method, PathString path)
    {
        if (!HttpMethods.IsPost(method) || !path.HasValue)
        {
            return false;
        }

        var value = path.Value!.TrimEnd('/');
        return Is(value, "/quickbooks/transactions")
               || Is(value, "/jobs")
               || (value.EndsWith("/post", Ordinal) && value.Contains("/jobs/", Ordinal))
               || (value.EndsWith("/undo", Ordinal) && value.Contains("/batches/", Ordinal));
    }

    /// <summary>
    /// T-910 (FR-A-13): the scope a route needs, or null when it needs no key at all (liveness and readiness).
    /// <para>
    /// Scopes name what a caller may <i>do</i>, so the mapping is by effect, not by URL shape: anything that sends a
    /// write to QuickBooks needs <c>qb:post</c>, anything that only reads needs <c>qb:read</c>. A route nobody has
    /// mapped falls through to <c>admin</c> — a new endpoint is closed until someone decides otherwise, which is the
    /// failure everybody survives.
    /// </para>
    /// </summary>
    public static string? ScopeFor(string method, PathString path)
    {
        if (!path.HasValue)
        {
            return ApiScopes.Admin;
        }

        var p = Flat(path.Value!).TrimEnd('/');
        var get = HttpMethods.IsGet(method);

        // Liveness and readiness answer without a key (spec §6); everything else under /health needs one.
        if (p is "/health" or "/health/ready")
        {
            return null;
        }

        if (p.StartsWith("/health", Ordinal))
        {
            return ApiScopes.HealthRead;
        }

        if (p.StartsWith("/rules/", Ordinal))
        {
            return ApiScopes.RulesWrite;
        }

        if (p.StartsWith("/jobs", Ordinal))
        {
            // Reading a job, or validating a folder without queueing it, is not the same right as starting one.
            return get || p.EndsWith("/validate", Ordinal) ? ApiScopes.JobsRead : ApiScopes.JobsWrite;
        }

        if (p.StartsWith("/batches/", Ordinal))
        {
            return p.EndsWith("/undo", Ordinal) ? ApiScopes.QbPost : ApiScopes.QbRead;
        }

        if (p == "/quickbooks/transactions")
        {
            // The only route that sends transactions to QuickBooks; its /validate sibling is caught below.
            return get ? ApiScopes.QbRead : ApiScopes.QbPost;
        }

        return p.StartsWith("/quickbooks/", Ordinal) || p == "/qb/sync-lists"
            ? ApiScopes.QbRead
            : ApiScopes.Admin;
    }

    /// <summary>The path without the version prefix, so one mapping serves both surfaces while T-901's flat routes live.</summary>
    private static string Flat(string path) =>
        path.StartsWith(V1Prefix, Ordinal) ? path[V1Prefix.Length..] : path;

    private const StringComparison Ordinal = StringComparison.OrdinalIgnoreCase;

    /// <summary>True for the flat path and its <c>/api/v1</c> form, and for neither of their longer relatives.</summary>
    private static bool Is(string path, string route) =>
        path.Equals(route, Ordinal) || path.Equals(V1Prefix + route, Ordinal);
}
