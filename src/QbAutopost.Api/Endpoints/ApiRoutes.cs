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

    private const StringComparison Ordinal = StringComparison.OrdinalIgnoreCase;

    /// <summary>True for the flat path and its <c>/api/v1</c> form, and for neither of their longer relatives.</summary>
    private static bool Is(string path, string route) =>
        path.Equals(route, Ordinal) || path.Equals(V1Prefix + route, Ordinal);
}
