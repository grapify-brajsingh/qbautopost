using QbAutopost.Core.Abstractions;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QbAutopost.Api.Database;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Api.Security;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.SavedRequests.Audit;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Endpoints.V2;

/// <summary>
/// Dashboard v2 spec 8: <c>GET /api/v2/audit-events</c> and <c>/audit-events.csv</c>, the feed over the view
/// <c>audit_events</c> (T-1106). The scope is <c>requests:read</c> (<see cref="ApiRoutes.ScopeFor"/>); user events are
/// kept for callers holding <c>users:manage</c> and silently dropped for everyone else, from the total too. The request
/// each row belongs to is joined here from the saved-request store, not in the view.
/// </summary>
public static class AuditEventEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>The CSV is read in pages of this many rows, so 50 000 rows never sit in memory at once.</summary>
    private const int CsvChunk = 1000;

    private static readonly string[] SingleValued =
        ["from", "to", "actor", "requestId", "reference", "pushId", "q", "page", "pageSize"];

    private static readonly Regex Instant = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    public static IEndpointRouteBuilder MapAuditEventEndpoints(this IEndpointRouteBuilder v2)
    {
        v2.MapGroup("/audit-events").RequireDatabase().MapGet(string.Empty, FeedAsync);
        v2.MapGroup("/audit-events.csv").RequireDatabase().MapGet(string.Empty, CsvAsync);
        return v2;
    }

    // ---- the feed ---------------------------------------------------------------------------------------------------

    private static async Task<IResult> FeedAsync(
        HttpContext http, ISavedRequestAuditStore audit, ISavedRequestStore requests, ActorDirectory actors, CancellationToken ct)
    {
        if (!TryParse(http, paged: true, out var query, out var error))
        {
            return BadRequest(error!);
        }

        var page = await audit.AuditEventsAsync(query!, ct);
        var names = await actors.ResolveAsync(page.Items.Select(e => (string?)e.ActorId), ct);
        var infos = new Dictionary<long, AuditRequestView?>();
        foreach (var id in page.Items.Where(e => e.RequestId is not null).Select(e => e.RequestId!.Value).Distinct())
        {
            infos[id] = await RequestOfAsync(requests, id, ct);
        }

        var items = page.Items.Select(e => ViewOf(e, names, infos)).ToList();
        return Results.Json(new PageView<AuditEventView>(items, page.Page, page.PageSize, page.Total), SavedRequestJson.Options);
    }

    private static AuditEventView ViewOf(
        AuditEvent e, ActorNames names, IReadOnlyDictionary<long, AuditRequestView?> requests) =>
        new(
            e.Id,
            DateTime.SpecifyKind(e.AtUtc, DateTimeKind.Utc),
            new AuditActorView(e.ActorId, names.Of(e.ActorId).Name, e.ActorKind),
            e.Action,
            e.RequestId is { } id ? requests.GetValueOrDefault(id) : null,
            e.RowIndex is null && e.RowExternalId is null ? null : new AuditRowView(e.RowExternalId, e.RowIndex),
            e.Field,
            e.Old,
            e.New,
            e.VersionNumber,
            e.Detail);

    /// <summary>One read per distinct request on the page (at most 200); the store has no batched summary read.</summary>
    private static async Task<AuditRequestView?> RequestOfAsync(ISavedRequestStore store, long id, CancellationToken ct) =>
        await store.GetAsync(id, ct) is { } r
            ? new AuditRequestView(r.Id, r.PublicRef, r.Reference, r.Metadata.PushId, r.Metadata.EmailSubject)
            : null;

    // ---- the CSV ----------------------------------------------------------------------------------------------------

    private static async Task<IResult> CsvAsync(
        HttpContext http, ISavedRequestAuditStore audit, ISavedRequestStore requests, ActorDirectory actors, IClock clock, CancellationToken ct)
    {
        if (!TryParse(http, paged: false, out var query, out var error))
        {
            return BadRequest(error!);
        }

        // Bound the export to its start time so rows written meanwhile cannot be duplicated or push it past the cap.
        query = query! with { To = query.To ?? clock.UtcNow };

        var count = await audit.AuditEventCountAsync(query, ct);
        if (count > AuditCsv.MaxRows)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Too many rows",
                detail: $"{count} rows match; a CSV holds at most {AuditCsv.MaxRows}. Narrow the filters (from, to, action, requestId).",
                type: "too-many-rows");
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = AuditCsv.ContentType;
        http.Response.Headers.ContentDisposition = "attachment; filename=\"audit-events.csv\"";

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var infos = new Dictionary<long, AuditRequestView?>();
        await AuditCsv.WriteAsync(
            http.Response.Body,
            ChunksAsync(audit, requests, actors, query, names, infos, ct),
            ct,
            e =>
            {
                var request = e.RequestId is { } id ? infos.GetValueOrDefault(id) : null;
                return new AuditCsvContext(
                    names.GetValueOrDefault(e.ActorId) ?? e.ActorId,
                    request?.Reference,
                    request?.PushId?.ToString("D"),
                    request?.PublicRef);
            });
        return Results.Empty;
    }

    /// <summary>Pages through the feed, resolving each row's actor name and request before yielding it.</summary>
    private static async IAsyncEnumerable<AuditEvent> ChunksAsync(
        ISavedRequestAuditStore audit, ISavedRequestStore requests, ActorDirectory actors, AuditQuery query,
        Dictionary<string, string> names, Dictionary<long, AuditRequestView?> infos,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var written = 0;
        for (var page = 1; ; page++)
        {
            var chunk = await audit.AuditEventsAsync(query with { Page = page, PageSize = CsvChunk }, ct);
            var resolved = await actors.ResolveAsync(chunk.Items.Select(e => (string?)e.ActorId), ct);
            foreach (var e in chunk.Items)
            {
                if (written++ >= AuditCsv.MaxRows)
                {
                    yield break;
                }

                names[e.ActorId] = resolved.Of(e.ActorId).Name;
                if (e.RequestId is { } id && !infos.ContainsKey(id))
                {
                    infos[id] = await RequestOfAsync(requests, id, ct);
                }

                yield return e;
            }

            if (chunk.Items.Count < CsvChunk)
            {
                yield break;
            }
        }
    }

    // ---- the query (spec 8.1) ---------------------------------------------------------------------------------------

    private static bool TryParse(HttpContext http, bool paged, out AuditQuery? query, out string? error)
    {
        query = null;
        error = null;
        var q = http.Request.Query;
        foreach (var name in SingleValued)
        {
            if (q[name].Count > 1)
            {
                error = $"{name} may be given once.";
                return false;
            }
        }

        if (!TryInstant(q, "from", out var from) || !TryInstant(q, "to", out var to))
        {
            error = "from and to are ISO 8601 instants with an offset, such as 2026-10-01T00:00:00Z or 2026-10-01T09:30:00+02:00 "
                + "(a + in a URL is written %2B).";
            return false;
        }

        long? requestId = null;
        if (q.TryGetValue("requestId", out var idText))
        {
            if (!SavedRequestId.TryParse(idText.ToString(), out var id))
            {
                error = "requestId is a number or SR-000123.";
                return false;
            }

            requestId = id;
        }

        Guid? pushId = null;
        if (q.TryGetValue("pushId", out var pushText))
        {
            if (!Guid.TryParseExact(pushText.ToString(), "D", out var guid))
            {
                error = "pushId is a full UUID.";
                return false;
            }

            pushId = guid;
        }

        var actions = new List<string>();
        foreach (var action in q["action"])
        {
            if (action is null || !AuditActions.All.Contains(action, StringComparer.Ordinal))
            {
                error = $"action must be one of {string.Join(", ", AuditActions.All)}.";
                return false;
            }

            actions.Add(action);
        }

        var page = 1;
        var pageSize = DefaultPageSize;
        if (paged)
        {
            if (q.TryGetValue("page", out var pageText)
                && (!int.TryParse(pageText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
            {
                error = "page must be 1 or more.";
                return false;
            }

            if (q.TryGetValue("pageSize", out var sizeText)
                && (!int.TryParse(sizeText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                    || pageSize < 1 || pageSize > MaxPageSize))
            {
                error = $"pageSize must be between 1 and {MaxPageSize}.";
                return false;
            }
        }

        // An API key never holds users:manage (people-only scope), so a key sees no user events.
        var caller = CallerMiddleware.CallerOf(http)!;
        query = new AuditQuery(
            from, to, Text(q, "actor"), requestId, Text(q, "reference"), pushId, actions, Text(q, "q"),
            caller.Allows(ApiScopes.UsersManage), page, pageSize);
        return true;
    }

    private static string? Text(IQueryCollection q, string name) =>
        q.TryGetValue(name, out var text) && !string.IsNullOrWhiteSpace(text.ToString()) ? text.ToString().Trim() : null;

    /// <summary>An instant that names its offset (<c>Z</c> or <c>+hh:mm</c>), as UTC. Absent is fine; a date alone or no offset is not.</summary>
    private static bool TryInstant(IQueryCollection q, string name, out DateTime? value)
    {
        value = null;
        if (!q.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!Instant.IsMatch(text.ToString())
            || !DateTimeOffset.TryParse(text.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query", detail: detail, type: "invalid-request");
}

public sealed record AuditActorView(string Id, string Name, string Kind);

public sealed record AuditRequestView(long Id, string PublicRef, string? Reference, Guid? PushId, string? EmailSubject);

public sealed record AuditRowView(string? ExternalId, int? Index);

/// <summary>Spec 8.2. <c>old</c>, <c>new</c> and <c>detail</c> are written as stored, null included.</summary>
public sealed record AuditEventView(
    string Id,
    DateTime AtUtc,
    AuditActorView Actor,
    string Action,
    AuditRequestView? Request,
    AuditRowView? Row,
    string? Field,
    JsonNode? Old,
    JsonNode? New,
    int? VersionNumber,
    JsonNode? Detail);
