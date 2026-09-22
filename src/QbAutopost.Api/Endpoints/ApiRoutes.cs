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
}
