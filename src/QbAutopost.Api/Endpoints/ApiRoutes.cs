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

    /// <summary>T-913 (FR-A-18): the hand-authored OpenAPI document, under <see cref="V1Prefix"/> only.</summary>
    public const string OpenApiPath = "/openapi.json";

    /// <summary>T-913 (FR-A-18): the Scalar reference UI, under <see cref="V1Prefix"/> only.</summary>
    public const string ReferencePath = "/reference";

    /// <summary>
    /// T-913 (FR-A-18): the documentation surface — the OpenAPI document, the Scalar page, and the page's own
    /// assets, which Scalar serves under the same prefix.
    /// <para>
    /// Unlike every other route question here, this one does not fall back to the flat path: the documentation
    /// describes <c>/api/v1</c> and is only ever served there, so the legacy surface of api-v1 §9 gains nothing new.
    /// </para>
    /// </summary>
    public static bool IsReference(PathString path) =>
        path.StartsWithSegments(V1Prefix + ReferencePath, StringComparison.OrdinalIgnoreCase)
        || (path.HasValue && path.Value!.TrimEnd('/').Equals(V1Prefix + OpenApiPath, Ordinal));

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
    /// T-912 (FR-A-16): the routes whose answer changes something, and which therefore leave an audit line.
    /// <para>
    /// A safe method never does. Three POSTs do not either, and they are named rather than inferred: a validate, a
    /// connection test and a company-file check all answer questions without writing anything, and burying the
    /// postings among them would make the audit file harder to read, not safer. A route nobody has listed is
    /// audited — the same way <see cref="ScopeFor"/> closes an unmapped route rather than opening it.
    /// </para>
    /// </summary>
    public static bool IsMutating(string method, PathString path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return false;
        }

        if (!path.HasValue)
        {
            return true;
        }

        var p = Flat(path.Value!).TrimEnd('/');
        return !ReadOnlyPosts.Contains(p);
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

        // T-913 (FR-A-18): the documentation, before the prefix is stripped — it exists on the versioned surface
        // only. api-v1 asks for health:read "in Production"; this asks for it everywhere, because a page that lists
        // every route, every scope and every request shape is not something to hand out by environment.
        // SPEC-GAP T-913 (Q-66): the stricter reading, as rule 4 requires when the spec allows two.
        if (IsReference(path))
        {
            return ApiScopes.HealthRead;
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

    /// <summary>The POSTs that answer a question without changing anything (see <see cref="IsMutating"/>).</summary>
    private static readonly HashSet<string> ReadOnlyPosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "/quickbooks/transactions/validate",
        "/quickbooks/connection/test",
        "/quickbooks/company-file/validate",
        "/jobs/validate",
    };

    /// <summary>True for the flat path and its <c>/api/v1</c> form, and for neither of their longer relatives.</summary>
    private static bool Is(string path, string route) =>
        path.Equals(route, Ordinal) || path.Equals(V1Prefix + route, Ordinal);
}
