namespace QbAutopost.Api.Database;

/// <summary>
/// <c>GET /api/v1/health/database</c> body (saved-requests spec 5.5). <paramref name="State"/> is the
/// <see cref="DatabaseState"/> name; <paramref name="Reason"/> is <c>not-configured</c>, <c>unreachable</c> or
/// <c>schema-out-of-date</c>, null when ok. <paramref name="LatencyMs"/> is the schema check's round trip, -1 when
/// none ran. No field can carry the connection string.
/// </summary>
public sealed record DatabaseHealthView(
    bool Ok,
    string State,
    string? Reason,
    bool Reachable,
    bool SchemaCurrent,
    IReadOnlyList<string> PendingMigrations,
    long LatencyMs);
