using Microsoft.EntityFrameworkCore;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Data.Entities;

namespace QbAutopost.Data;

/// <summary>
/// The audit feed (T-1106, spec saved-requests-dashboard-v2 8) over the view <c>audit_events</c>. Every value of the
/// query is a parameter; the view does the mapping, so this part only filters, orders and pages.
/// </summary>
public sealed partial class EfSavedRequestStore : ISavedRequestAuditStore
{
    // Ordinal (byte) order for the id tie-break, whatever the database's collation, as the fake's StringComparer.Ordinal.
    private const string OrdinalCollation = "C";

    public async Task<PagedResult<AuditEvent>> AuditEventsAsync(AuditQuery query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = Filter(db, query);

        var total = await rows.CountAsync(ct);
        var size = Math.Max(1, query.PageSize);
        var page = Math.Max(1, query.Page);
        var items = await rows
            .OrderByDescending(e => e.AtUtc)
            .ThenByDescending(e => EF.Functions.Collate(e.Id, OrdinalCollation))
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return new PagedResult<AuditEvent>(items.Select(ToModel).ToList(), page, size, total);
    }

    public async Task<int> AuditEventCountAsync(AuditQuery query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Filter(db, query).CountAsync(ct);
    }

    private static IQueryable<AuditEventRow> Filter(QbAutopostDbContext db, AuditQuery query)
    {
        IQueryable<AuditEventRow> rows = db.AuditEvents.AsNoTracking();

        if (!query.IncludeUserEvents) rows = rows.Where(e => !EF.Functions.Like(e.Id, "u%"));
        if (query.From is { } from) rows = rows.Where(e => e.AtUtc >= from);
        if (query.To is { } to) rows = rows.Where(e => e.AtUtc < to);
        if (query.Actor is { Length: > 0 } actor) rows = rows.Where(e => e.ActorId == actor);
        if (query.RequestId is { } requestId) rows = rows.Where(e => e.RequestId == requestId);
        if (query.Reference is { Length: > 0 } reference)
        {
            rows = rows.Where(e => db.SavedRequests.Any(r => r.Id == e.RequestId && r.Reference == reference));
        }

        if (query.PushId is { } pushId)
        {
            rows = rows.Where(e => db.SavedRequests.Any(r => r.Id == e.RequestId && r.PushId == pushId));
        }

        if (query.Actions.Count > 0)
        {
            var actions = query.Actions.ToList();
            rows = rows.Where(e => actions.Contains(e.Action));
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var matches = AuditMatches(db, query.Q.Trim());
            rows = rows.Where(e => matches.Contains(e.Id));
        }

        return rows;
    }

    /// <summary>Ids of feed rows whose old or new JSON text, field, or request reference, public ref or push id hex contains <paramref name="q"/>, ignoring case.</summary>
    private static IQueryable<string> AuditMatches(QbAutopostDbContext db, string q)
    {
        var pattern = "%" + EscapeLike(q) + "%";
        return db.Database.SqlQuery<string>(
            $"""
            SELECT a.id AS "Value"
            FROM audit_events a
            LEFT JOIN saved_requests s ON s.id = a.request_id
            WHERE a.old::text ILIKE {pattern} ESCAPE '\'
               OR a.new::text ILIKE {pattern} ESCAPE '\'
               OR a.field ILIKE {pattern} ESCAPE '\'
               OR s.reference ILIKE {pattern} ESCAPE '\'
               OR s.public_ref ILIKE {pattern} ESCAPE '\'
               OR s.push_id_hex ILIKE {pattern} ESCAPE '\'
            """);
    }

    private static AuditEvent ToModel(AuditEventRow r) => new(
        r.Id, r.AtUtc, r.ActorId, r.ActorKind, r.Action, r.RequestId, r.RowIndex, r.RowExternalId, r.Field,
        ParseNode(r.Old), ParseNode(r.New), r.VersionNumber, ParseNode(r.Detail));
}
