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

    /// <summary>
    /// T-1109 (dashboard API v2, spec 2 and 3): the dashboard surface. Only the routes in <see cref="V2Routes"/> exist
    /// under it; every other <c>/api/v2</c> path keeps its prefix in <see cref="ScopeFor"/> and so falls through to
    /// <c>admin</c>.
    /// </summary>
    public const string V2Prefix = "/api/v2";

    /// <summary>
    /// T-1109 (spec 3): every route under <see cref="V2Prefix"/>, relative to it, by method. A path is read as a v2
    /// route, with its prefix stripped, only when it has the shape of one of these templates (a <c>{name}</c> segment
    /// matches any one segment). A route added to the v2 group must be added here in the same change, or it is closed
    /// to everyone but <c>admin</c>.
    /// </summary>
    public static IReadOnlyList<(string Method, string Template)> V2Routes { get; } =
    [
        ("POST", "/saved-requests"),
        ("GET", "/saved-requests"),
        ("GET", "/saved-requests/stats"),
        ("GET", "/saved-requests/{id}"),
        ("PUT", "/saved-requests/{id}"),
        ("POST", "/saved-requests/{id}/validate"),
        ("POST", "/saved-requests/{id}/discard"),
        ("POST", "/saved-requests/{id}/restore"),
        ("POST", "/saved-requests/{id}/post"),
        ("POST", "/saved-requests/{id}/resolve"),
        ("GET", "/saved-requests/{id}/original"),
        ("GET", "/saved-requests/{id}/versions"),
        ("GET", "/saved-requests/{id}/versions/{n}"),
        ("GET", "/saved-requests/{id}/attempts"),
        ("GET", "/saved-requests/{id}/attempts/{attemptId}"),
        ("GET", "/saved-requests/{id}/events"),
        ("GET", "/audit-events"),
        ("GET", "/audit-events.csv"),
        ("GET", "/dashboard/summary"),
        ("POST", "/statement-extractions"),
        ("GET", "/users"),
        ("POST", "/users"),
        ("GET", "/users/{id}"),
        ("PATCH", "/users/{id}"),
        ("POST", "/users/{id}/reset-password"),
        ("POST", "/users/{id}/unlock"),
        ("GET", "/users/{id}/audit"),
        ("POST", "/auth/login"),
        ("POST", "/auth/refresh"),
        ("POST", "/auth/logout"),
        ("POST", "/auth/change-password"),
        ("GET", "/auth/me"),
        ("GET", "/quickbooks/lists"),
        ("POST", ListSyncRoute),
        ("GET", "/health"),
        ("GET", "/health/database"),
        ("GET", OpenApiPath),
    ];

    /// <summary>
    /// T-924 (ADR-0008): the synchronous validate-and-post, flat. It posts, so it is named in <see cref="ScopeFor"/>
    /// and <see cref="IsIdempotent"/> and left out of <c>ReadOnlyPosts</c> — one constant, so the three cannot
    /// disagree about which path they mean.
    /// </summary>
    public const string SyncPostRoute = "/quickbooks/transactions/validate-and-post";

    /// <summary>
    /// M14 §7.2: the list sync. Under <c>/api/v1</c> (and flat) it is v1's, <c>qb:read</c>; under <c>/api/v2</c> it is the
    /// dashboard's Sync now (<see cref="IsV2ListSync"/>), with its own scope and the posting rate bucket.
    /// </summary>
    public const string ListSyncRoute = "/quickbooks/lists/sync";

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
    /// <para>
    /// T-1109 (dashboard v2 spec 15): the v2 document at <c>/api/v2/openapi.json</c> is served under the same rules.
    /// There is no v2 reference page.
    /// </para>
    /// </summary>
    public static bool IsReference(PathString path) =>
        path.StartsWithSegments(V1Prefix + ReferencePath, StringComparison.OrdinalIgnoreCase)
        || (path.HasValue && path.Value!.TrimEnd('/').Equals(V1Prefix + OpenApiPath, Ordinal))
        || (path.HasValue && path.Value!.TrimEnd('/').Equals(V2Prefix + OpenApiPath, Ordinal));

    /// <summary>
    /// T-1116 (dashboard v2 spec 13.1, R-2): the first segment of every flat route that <c>Api:LegacyRoutes</c> maps
    /// beside <c>/api/v1</c>. A route added to the flat surface must start with one of these, or the dashboard would
    /// answer its path (pinned by <c>DashboardHostingTests</c>).
    /// </summary>
    public static IReadOnlyList<string> LegacyFlatPrefixes { get; } =
        ["/health", "/jobs", "/batches", "/quickbooks", "/qb", "/rules"];

    /// <summary>The root of every versioned surface; nothing under it is ever the dashboard's.</summary>
    private const string ApiRoot = "/api";

    /// <summary>
    /// T-1116 (dashboard v2 spec 13.1): true for a path the dashboard serves. Neither under <c>/api</c> nor under any
    /// <see cref="LegacyFlatPrefixes"/> entry, whether or not the legacy routes are switched on, so turning them off
    /// never hands their paths to the dashboard. Matched by whole segment, ignoring case as routing does: <c>/jobs/1</c>
    /// is the API's, <c>/jobsite</c> is the dashboard's.
    /// </summary>
    public static bool IsDashboardPath(PathString path)
    {
        if (path.StartsWithSegments(ApiRoot, Ordinal))
        {
            return false;
        }

        foreach (var prefix in LegacyFlatPrefixes)
        {
            if (path.StartsWithSegments(prefix, Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True for a health route under either prefix. Health needs no API key (spec §6) and a healthy poll is logged at
    /// Debug (spec §14), so both places ask here rather than testing one prefix and forgetting the other.
    /// <para>
    /// T-1109 (dashboard v2 spec 2, ruling R8): the <c>/api/v2/health</c> prefix too, so v2's
    /// <c>/health/database</c> is rate-limited and logged exactly as v1's. Only the clause below is new.
    /// </para>
    /// </summary>
    public static bool IsHealth(PathString path) =>
        path.StartsWithSegments(HealthPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(V1Prefix + HealthPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(V2Prefix + HealthPrefix, StringComparison.OrdinalIgnoreCase);

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
        // T-924: the synchronous post is here for two reasons at once. §3 honours Idempotency-Key on it — the only
        // mechanism that returns the *original* txnIds after a lost response (§8) — and RateLimitPolicy reads this
        // same predicate, so naming it here also puts it in the posting bucket rather than the cheaper default one.
        //
        // T-1012: POST /saved-requests is deliberately absent. Its Idempotency-Key is honoured by the store, per source
        // and in the same transaction as the save (saved-requests spec 10.1): same key and body answer 200 with the
        // existing record, a different body 409 idempotency-key-reused. This middleware's file-backed replay would
        // answer from a second, separate record of the same fact, and the two could disagree.
        return Is(value, "/quickbooks/transactions")
               || Is(value, SyncPostRoute)
               || Is(value, "/jobs")
               || (value.EndsWith("/post", Ordinal) && value.Contains("/jobs/", Ordinal))
               || (value.EndsWith("/undo", Ordinal) && value.Contains("/batches/", Ordinal));
    }

    /// <summary>
    /// M10 review: <c>POST /saved-requests/{id}/post</c>, which sends a saved request through validate-and-post's engine.
    /// It moves money, so <c>RateLimitPolicy</c> puts it in the posting bucket, but it is deliberately not
    /// <see cref="IsIdempotent"/>: the store's claim is what stops a second send (saved-requests spec 10.2), and the
    /// replay middleware answering from a file would be a second record of the same fact.
    /// </summary>
    public static bool IsSavedRequestPost(string method, PathString path)
    {
        if (!HttpMethods.IsPost(method) || !path.HasValue)
        {
            return false;
        }

        var segments = Flat(path.Value!).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3
               && segments[0].Equals("saved-requests", Ordinal)
               && segments[2].Equals("post", Ordinal);
    }

    /// <summary>
    /// M14 §7.2: <c>POST /api/v2/quickbooks/lists/sync</c>, the dashboard's Sync now. It opens a QuickBooks session, so
    /// <c>RateLimitPolicy</c> puts it in the posting bucket. The v1 and flat spellings are not it: they keep v1's bucket.
    /// Case-insensitive, as routing is (R32; spelled out here because <c>Ordinal</c> in this class already ignores case).
    /// </summary>
    public static bool IsV2ListSync(string method, PathString path) =>
        HttpMethods.IsPost(method)
        && path.HasValue
        && path.Value!.TrimEnd('/').Equals(V2Prefix + ListSyncRoute, StringComparison.OrdinalIgnoreCase);

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
    /// T-910 (FR-A-13): the scope a route needs, or null when it needs no credential at all (liveness, readiness, and
    /// since T-1009 <c>/auth/login</c> and <c>/auth/refresh</c>). A value may name alternatives
    /// (<see cref="ApiScopes.AnyOf"/>), and <see cref="ApiScopes.SignedIn"/> asks for any signed-in person.
    /// <para>
    /// Scopes name what a caller may <i>do</i>, so the mapping is by effect, not by URL shape: anything that sends a
    /// write to QuickBooks needs <c>qb:post</c>, anything that only reads needs <c>qb:read</c>. A route nobody has
    /// mapped falls through to <c>admin</c> — a new endpoint is closed until someone decides otherwise, which is the
    /// failure everybody survives.
    /// </para>
    /// </summary>
    public static string? ScopeFor(string method, PathString path, bool isDevelopment = false)
    {
        if (!path.HasValue)
        {
            return ApiScopes.Admin;
        }

        // T-913 (FR-A-18): the documentation, before the prefix is stripped — it exists on the versioned surface
        // only. T-913 asked for health:read in every environment, the stricter of the two readings the spec allows.
        //
        // T-918 (Q-66, ANSWERED by the owner 2026-09-23): the spec's literal rule instead — health:read "in
        // Production", nothing in Development. The strictness had a cost Q-66 had already written down and the live
        // run then confirmed: the key travels in a header, so a browser cannot attach it, and the one page whose
        // entire purpose is to be opened in a browser answered 401 to every browser. Development is the developer's
        // own machine, and TransportGuard still refuses a non-loopback bind there without TLS.
        //
        // This opens the documentation and nothing else: every other route keeps its scope in every environment.
        if (IsReference(path))
        {
            return isDevelopment ? null : ApiScopes.HealthRead;
        }

        var p = Flat(path.Value!).TrimEnd('/');
        var get = HttpMethods.IsGet(method);

        // T-1212b (dashboard-api-m12 8, 14.1): v2's /health is the dashboard's view of the server (the upload switch, the
        // job model), so like /health/database it needs a dashboard user or a monitor. v1's stays keyless, unchanged.
        if (p.Equals("/health", Ordinal) && path.Value!.StartsWith(V2Prefix + "/", Ordinal))
        {
            return get ? ApiScopes.AnyOf(ApiScopes.RequestsRead, ApiScopes.HealthRead) : ApiScopes.Admin;
        }

        // Liveness and readiness answer without a key (spec §6); everything else under /health needs one.
        // R32: every exact match below ignores case, as routing does. A case-sensitive `is`/`==` here once let a
        // read-only key post: /QuickBooks/Transactions/Validate-And-Post missed its branch and fell through to qb:read.
        if (p.Equals("/health", Ordinal) || p.Equals("/health/ready", Ordinal))
        {
            return null;
        }

        // Named so the database route is a decision, not a side effect of the prefix below. Saved-requests spec 11.3:
        // either scope, so the dashboard (requests:read) can show a status light and monitoring (health:read) still can.
        if (p.Equals("/health/database", Ordinal))
        {
            return ApiScopes.AnyOf(ApiScopes.RequestsRead, ApiScopes.HealthRead);
        }

        if (p.StartsWith("/health", Ordinal))
        {
            return ApiScopes.HealthRead;
        }

        // T-1009 (saved-requests spec 6, 11.2, 11.3, 11.4): mapped explicitly, before the /quickbooks/ fall-through
        // below can open anything, and each falls back to admin rather than to a neighbour's scope.
        if (IsUnder(p, "/saved-requests"))
        {
            return SavedRequestScope(method, p);
        }

        // T-1114 (dashboard v2 spec 8.1, 13.1): the audit feed and its CSV are reads of saved-request history. They exist
        // under /api/v2 only, so the original path must carry that prefix: /api/v1/audit-events and the flat form are
        // unmapped and keep the admin fall-through. Only a GET is mapped; anything else on these paths stays admin.
        if ((p.Equals("/audit-events", Ordinal) || p.Equals("/audit-events.csv", Ordinal))
            && path.Value!.StartsWith(V2Prefix + "/", Ordinal))
        {
            return get ? ApiScopes.RequestsRead : ApiScopes.Admin;
        }

        // T-1212a (dashboard-api-m12 4.1): the Home summary is a read of saved-request history, for every role. Like the
        // audit feed it exists under /api/v2 only, so /api/v1/dashboard/summary and the flat form keep the admin
        // fall-through. Only a GET is mapped; anything else on the path stays admin.
        if (p.Equals("/dashboard/summary", Ordinal) && path.Value!.StartsWith(V2Prefix + "/", Ordinal))
        {
            return get ? ApiScopes.RequestsRead : ApiScopes.Admin;
        }

        // T-1212b (dashboard-api-m12 6.1): the PDF upload, under /api/v2 only, like the summary. requests:submit, and the
        // endpoint also requires a signed-in user (an API key gets 403 there). Anything but a POST stays admin.
        if (p.Equals("/statement-extractions", Ordinal) && path.Value!.StartsWith(V2Prefix + "/", Ordinal))
        {
            return HttpMethods.IsPost(method) ? ApiScopes.RequestsSubmit : ApiScopes.Admin;
        }

        if (IsUnder(p, "/users"))
        {
            return ApiScopes.UsersManage;
        }

        if (IsUnder(p, "/auth"))
        {
            return AuthScope(method, p);
        }

        // M14 §7.2 (Q-120): the dashboard's Sync now, under /api/v2 only. Editors and admins (requests:edit, which no key
        // can hold), or an API key with qb:read as on v1. Its v1 and flat spellings fall through to qb:read below.
        if (IsV2ListSync(method, path))
        {
            return ApiScopes.AnyOf(ApiScopes.RequestsEdit, ApiScopes.QbRead);
        }

        // Spec 11.1: the edit dropdowns read the cached lists, and that is all lists:read opens. Syncing the lists,
        // which opens a QuickBooks session, stays qb:read below.
        if (get && p.Equals("/quickbooks/lists", Ordinal))
        {
            return ApiScopes.ListsRead;
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

        if (p.Equals("/quickbooks/transactions", Ordinal) || p.Equals(SyncPostRoute, Ordinal))
        {
            // The two routes that send transactions to QuickBooks; the /validate sibling is caught below.
            //
            // T-924: validate-and-post is named here because the fall-through beneath it *opens* rather than closes.
            // Every unmapped path lands on admin — a new route is shut until somebody decides otherwise — but
            // anything beginning /quickbooks/ resolves to qb:read, so a route that posts real money would have been
            // gated behind the scope issued for reading lists: silently, with every test still green. It is matched
            // by its exact path rather than by a shape such as EndsWith("/post"), because this name ends in "post"
            // while the read-only /validate contains "validate" — any pattern clever enough to catch the one would
            // misfile the other.
            return get ? ApiScopes.QbRead : ApiScopes.QbPost;
        }

        return p.StartsWith("/quickbooks/", Ordinal) || p.Equals("/qb/sync-lists", Ordinal)
            ? ApiScopes.QbRead
            : ApiScopes.Admin;
    }

    /// <summary>
    /// Spec 11.2: the routes a user whose <c>mustChangePassword</c> is set may still call; every other route answers
    /// <c>403 password-change-required</c>.
    /// </summary>
    public static bool AllowsPendingPasswordChange(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        var p = Flat(path.Value!).TrimEnd('/');
        return p.Equals(AuthMe, Ordinal) || p.Equals(AuthChangePassword, Ordinal) || p.Equals(AuthLogout, Ordinal);
    }

    private const string AuthMe = "/auth/me";
    private const string AuthChangePassword = "/auth/change-password";
    private const string AuthLogout = "/auth/logout";

    /// <summary>Spec 6 and 11.3: every GET reads; each change is named; anything else is admin, as everywhere.</summary>
    private static string SavedRequestScope(string method, string p)
    {
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (HttpMethods.IsGet(method))
        {
            return ApiScopes.RequestsRead;
        }

        return (segments.Length, method.ToUpperInvariant()) switch
        {
            (1, "POST") => ApiScopes.RequestsSubmit,
            (2, "PUT") when !segments[1].Equals("stats", Ordinal) => ApiScopes.RequestsEdit,
            (3, "POST") => segments[2].ToLowerInvariant() switch
            {
                "validate" or "discard" => ApiScopes.RequestsEdit,
                "post" => ApiScopes.RequestsPost,
                "restore" or "resolve" => ApiScopes.RequestsAdmin,
                _ => ApiScopes.Admin,
            },
            _ => ApiScopes.Admin,
        };
    }

    /// <summary>Spec 11.2: login and refresh need no credential; me, logout and change-password need a signed-in person.</summary>
    private static string? AuthScope(string method, string p)
    {
        var post = HttpMethods.IsPost(method);
        if (post && (p.Equals("/auth/login", Ordinal) || p.Equals("/auth/refresh", Ordinal)))
        {
            return null;
        }

        return (HttpMethods.IsGet(method) && p.Equals(AuthMe, Ordinal))
               || (post && (p.Equals(AuthLogout, Ordinal) || p.Equals(AuthChangePassword, Ordinal)))
            ? ApiScopes.SignedIn
            : ApiScopes.Admin;
    }

    /// <summary>The route itself or anything below it, and not a longer sibling such as <c>/usersx</c>.</summary>
    private static bool IsUnder(string path, string route) =>
        path.Equals(route, Ordinal) || path.StartsWith(route + "/", Ordinal);

    /// <summary>
    /// The path without the version prefix, so one mapping serves both surfaces while T-901's flat routes live.
    /// T-1109: the <c>/api/v2</c> prefix is stripped only from a path that has the shape of a <see cref="V2Routes"/>
    /// template, so an unknown v2 path keeps its prefix and lands on <c>admin</c>. v1 paths are matched as before.
    /// </summary>
    private static string Flat(string path)
    {
        if (path.StartsWith(V1Prefix, Ordinal))
        {
            return path[V1Prefix.Length..];
        }

        if (path.StartsWith(V2Prefix + "/", Ordinal) && IsV2Route(path[V2Prefix.Length..]))
        {
            return path[V2Prefix.Length..];
        }

        return path;
    }

    /// <summary>True when <paramref name="relative"/> (the path after <c>/api/v2</c>) has the shape of a v2 route template.</summary>
    private static bool IsV2Route(string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (_, template) in V2Routes)
        {
            var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != segments.Length)
            {
                continue;
            }

            var match = true;
            for (var i = 0; i < parts.Length && match; i++)
            {
                match = parts[i].StartsWith('{') || parts[i].Equals(segments[i], Ordinal);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>R32: every comparison in this class. Routing matches paths without regard to case, so these must too.</summary>
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
