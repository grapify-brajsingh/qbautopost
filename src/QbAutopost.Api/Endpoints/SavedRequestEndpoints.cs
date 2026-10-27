using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Api.Security;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// Saved-requests spec 6: n8n saves a validate-and-post body here instead of posting it, and people read it in the
/// dashboard. Served under <c>/api/v1</c> only, behind the database gate (spec 10.7: <c>503 database-unavailable</c>
/// with <c>Retry-After: 30</c>, before any store call). The scope of each route is <see cref="ApiRoutes.ScopeFor"/>'s.
/// <para>
/// T-1012 maps the save and the reads. Edit, validate, discard and restore are T-1013; post and resolve are T-1014.
/// </para>
/// <para>
/// Every answer is written with <see cref="SavedRequestJson.Options"/> (kebab-case enums), and every <c>{id}</c> goes
/// through <see cref="SavedRequestId.TryParse"/>: something that is not an id is <c>404</c>, like an unknown one.
/// </para>
/// </summary>
public static class SavedRequestEndpoints
{
    private const int DefaultPageSize = 25;
    private const string DefaultSort = "-created";

    private static readonly HashSet<string> Sorts = new(StringComparer.Ordinal)
    {
        "created", "-created", "updated", "-updated", "controlTotal", "-controlTotal",
    };

    private static readonly Dictionary<string, SavedRequestStatus> StatusNames =
        Enum.GetValues<SavedRequestStatus>().ToDictionary(s => SavedRequestJson.Name(s), StringComparer.Ordinal);

    /// <summary>Strict UTF-8: a body that is not valid UTF-8 cannot be stored "as received, as UTF-8" (spec 5.2).</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IEndpointRouteBuilder MapSavedRequestEndpoints(this IEndpointRouteBuilder v1)
    {
        var group = v1.MapGroup("/saved-requests").RequireDatabase();
        group.MapPost("/", SaveAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/stats", StatsAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapGet("/{id}/original", OriginalAsync);
        group.MapGet("/{id}/versions", VersionsAsync);
        group.MapGet("/{id}/versions/{n}", VersionAsync);
        group.MapGet("/{id}/attempts", AttemptsAsync);
        group.MapGet("/{id}/attempts/{attemptId}", AttemptAsync);
        group.MapGet("/{id}/events", EventsAsync);
        return v1;
    }

    // ---- save -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Spec 6.1. The size limit (step 1, 413) is <c>RequestSizeMiddleware</c>'s, before this runs. Not an object
    /// (step 2) is <c>400 not-a-json-object</c> and nothing is stored. Any object is stored (steps 3-5).
    /// <para>
    /// 10.1: an <c>Idempotency-Key</c> is honoured per source by the store, not by <c>IdempotencyMiddleware</c>: the
    /// same key and body answer 200 with the existing record, the same key with another body
    /// <c>409 idempotency-key-reused</c>.
    /// </para>
    /// </summary>
    private static async Task<IResult> SaveAsync(HttpContext http, SavedRequestIntake intake, CancellationToken ct)
    {
        string raw;
        try
        {
            http.Request.EnableBuffering();
            http.Request.Body.Position = 0;
            using var reader = new StreamReader(http.Request.Body, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            raw = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            return NotAJsonObject("The body is not valid UTF-8.");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body declares no length, so only the server can stop it mid-flight (RequestSizeMiddleware).
            return Problem(StatusCodes.Status413PayloadTooLarge, "Request body too large", ex.Message, "payload-too-large");
        }

        if (!SavedRequestIntake.TryParseObject(raw, out _))
        {
            return NotAJsonObject("The body must be one JSON object: the validate-and-post body (validate-and-post §4).");
        }

        var key = http.Request.Headers[IdempotencyMiddleware.KeyHeader].ToString().Trim();
        if (key.Length > IdempotencyMiddleware.MaxKeyLength)
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Idempotency-Key too long",
                $"An Idempotency-Key may be at most {IdempotencyMiddleware.MaxKeyLength} characters.", "invalid-request");
        }

        var caller = CallerMiddleware.CallerOf(http)!;
        var result = await intake.SaveAsync(raw, key.Length == 0 ? null : key, caller, ct);
        var record = result.Record;
        switch (result.Kind)
        {
            case CreateKind.KeyReused:
                return Problem(
                    StatusCodes.Status409Conflict, "Idempotency-Key already used",
                    $"This Idempotency-Key was already used for {record.PublicRef} with a different body. Nothing was saved. "
                    + "Send a new key for a new request.",
                    "idempotency-key-reused");
            case CreateKind.Replayed:
                http.Response.Headers[IdempotencyMiddleware.ReplayedHeader] = "true";
                return Json(SavedRequestViews.Saved(record));
            default:
                http.Response.Headers.Location =
                    $"{ApiRoutes.V1Prefix}/saved-requests/{record.Id.ToString(CultureInfo.InvariantCulture)}";
                return Results.Json(
                    SavedRequestViews.Saved(record), SavedRequestJson.Options, statusCode: StatusCodes.Status201Created);
        }
    }

    // ---- list and stats ---------------------------------------------------------------------------------------------

    /// <summary>Spec 6.5. Every query value is checked; one that is not understood is <c>400</c>, never ignored.</summary>
    private static async Task<IResult> ListAsync(
        HttpContext http, ISavedRequestStore store, ActorDirectory actors, IOptions<AppSettings> settings, CancellationToken ct)
    {
        var query = http.Request.Query;
        var statuses = new List<SavedRequestStatus>();
        foreach (var text in query["status"])
        {
            if (text is null || !StatusNames.TryGetValue(text, out var status))
            {
                return BadRequest("Unknown status", $"status must be one of {string.Join(", ", StatusNames.Keys)}.");
            }

            statuses.Add(status);
        }

        if (!TryBool(query, "needsAttention", out var needsAttention) || !TryBool(query, "duplicate", out var duplicate))
        {
            return BadRequest("Invalid flag", "needsAttention and duplicate must be true or false.");
        }

        if (!TryDate(query, "createdFrom", out var createdFrom) || !TryDate(query, "createdTo", out var createdTo))
        {
            return BadRequest("Invalid date", "createdFrom and createdTo are dates, yyyy-MM-dd (UTC).");
        }

        var sort = query.TryGetValue("sort", out var sortText) ? sortText.ToString() : DefaultSort;
        if (!Sorts.Contains(sort))
        {
            return BadRequest("Unknown sort", $"sort must be one of {string.Join(", ", Sorts)}.");
        }

        // The store has no cap; the route holds pageSize to SavedRequests:MaxListPageSize.
        var max = Math.Max(1, settings.Value.SavedRequests.MaxListPageSize);
        if (Paging(query, max) is not { } paging)
        {
            return BadRequest("Invalid paging", $"page must be 1 or more and pageSize between 1 and {max}.");
        }

        var q = query.TryGetValue("q", out var qText) && !string.IsNullOrWhiteSpace(qText.ToString()) ? qText.ToString().Trim() : null;
        var source = query.TryGetValue("source", out var sourceText) && !string.IsNullOrWhiteSpace(sourceText.ToString())
            ? sourceText.ToString().Trim()
            : null;

        var page = await store.ListAsync(
            new SavedRequestQuery(statuses, needsAttention, duplicate, q, createdFrom, createdTo, source, sort, paging.Page, paging.PageSize),
            ct);
        var names = await actors.ResolveAsync(page.Items.SelectMany(SavedRequestViews.ActorsOf), ct);
        var caller = CallerMiddleware.CallerOf(http)!;
        var items = page.Items.Select(s => SavedRequestViews.Summary(s, caller, names)).ToList();
        return Json(new PageView<SavedRequestSummaryView>(items, page.Page, page.PageSize, page.Total));
    }

    private static async Task<IResult> StatsAsync(ISavedRequestStore store, CancellationToken ct) =>
        Json(SavedRequestViews.Stats(await store.StatsAsync(ct)));

    // ---- one request ------------------------------------------------------------------------------------------------

    /// <summary>Spec 6.2. The <c>ETag</c> is the row version; <c>allowedActions</c> follow the status and the caller's scopes.</summary>
    private static async Task<IResult> GetAsync(
        HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (await FindAsync(store, id, ct) is not { } record)
        {
            return NotFound();
        }

        var version = await store.VersionAsync(record.Id, record.CurrentVersion, ct);
        var names = await actors.ResolveAsync(SavedRequestViews.ActorsOf(record), ct);
        http.Response.Headers.ETag = $"\"{record.RowVersion.ToString(CultureInfo.InvariantCulture)}\"";
        return Json(SavedRequestViews.Detail(record, version?.ReadErrors ?? [], CallerMiddleware.CallerOf(http)!, names));
    }

    /// <summary>Spec 5.1, 6: the body exactly as it was received, byte for byte; never re-serialised.</summary>
    private static async Task<IResult> OriginalAsync(string id, ISavedRequestStore store, CancellationToken ct) =>
        await FindAsync(store, id, ct) is { } record
            ? Results.Bytes(StrictUtf8.GetBytes(record.OriginalBody), "application/json; charset=utf-8")
            : NotFound();

    private static async Task<IResult> VersionsAsync(string id, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (await FindAsync(store, id, ct) is not { } record)
        {
            return NotFound();
        }

        var versions = (await store.VersionsAsync(record.Id, ct)).OrderByDescending(v => v.Number).ToList();
        var names = await actors.ResolveAsync(versions.Select(v => v.CreatedBy), ct);
        return Json(new ItemsView<VersionSummaryView>(versions.Select(v => SavedRequestViews.VersionSummary(v, names)).ToList()));
    }

    private static async Task<IResult> VersionAsync(
        string id, string n, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1
            || !SavedRequestId.TryParse(id, out var requestId)
            || await store.VersionAsync(requestId, number, ct) is not { } version)
        {
            return NotFound();
        }

        var names = await actors.ResolveAsync([version.CreatedBy], ct);
        return Json(SavedRequestViews.Version(version, names));
    }

    private static async Task<IResult> AttemptsAsync(string id, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (await FindAsync(store, id, ct) is not { } record)
        {
            return NotFound();
        }

        var attempts = (await store.AttemptsAsync(record.Id, ct)).OrderByDescending(a => a.StartedUtc).ThenByDescending(a => a.Id).ToList();
        var names = await actors.ResolveAsync(attempts.SelectMany(a => new[] { a.StartedBy, a.ResolvedBy }), ct);
        return Json(new ItemsView<AttemptView>(attempts.Select(a => SavedRequestViews.Attempt(a, names)).ToList()));
    }

    private static async Task<IResult> AttemptAsync(
        string id, string attemptId, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (!long.TryParse(attemptId, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1
            || !SavedRequestId.TryParse(id, out var requestId)
            || await store.AttemptAsync(requestId, attempt, ct) is not { } found)
        {
            return NotFound();
        }

        var names = await actors.ResolveAsync([found.StartedBy, found.ResolvedBy], ct);
        return Json(SavedRequestViews.AttemptDetail(found, names));
    }

    private static async Task<IResult> EventsAsync(string id, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (await FindAsync(store, id, ct) is not { } record)
        {
            return NotFound();
        }

        var events = (await store.EventsAsync(record.Id, ct)).OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id).ToList();
        var names = await actors.ResolveAsync(events.Select(e => e.Actor), ct);
        return Json(new ItemsView<EventView>(events.Select(e => SavedRequestViews.Event(e, names)).ToList()));
    }

    // ---- shared steps -----------------------------------------------------------------------------------------------

    private static async Task<SavedRequest?> FindAsync(ISavedRequestStore store, string id, CancellationToken ct) =>
        SavedRequestId.TryParse(id, out var value) ? await store.GetAsync(value, ct) : null;

    private static bool TryBool(IQueryCollection query, string name, out bool? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!bool.TryParse(text.ToString(), out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryDate(IQueryCollection query, string name, out DateOnly? value)
    {
        value = null;
        if (!query.TryGetValue(name, out var text))
        {
            return true;
        }

        if (!DateOnly.TryParseExact(text.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>(page, pageSize), or null when either is not a whole number in range.</summary>
    private static (int Page, int PageSize)? Paging(IQueryCollection query, int maxPageSize)
    {
        var page = 1;
        var pageSize = Math.Min(DefaultPageSize, maxPageSize);
        if (query.TryGetValue("page", out var pageText)
            && (!int.TryParse(pageText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            return null;
        }

        if (query.TryGetValue("pageSize", out var sizeText)
            && (!int.TryParse(sizeText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1 || pageSize > maxPageSize))
        {
            return null;
        }

        return (page, pageSize);
    }

    private static IResult Json<T>(T value) => Results.Json(value, SavedRequestJson.Options);

    private static IResult Problem(int status, string title, string detail, string type) =>
        Results.Problem(statusCode: status, title: title, detail: detail, type: type);

    private static IResult NotAJsonObject(string detail) =>
        Problem(StatusCodes.Status400BadRequest, "Not a JSON object", detail + " Nothing was saved.", "not-a-json-object");

    private static IResult BadRequest(string title, string detail) =>
        Problem(StatusCodes.Status400BadRequest, title, detail, "invalid-request");

    private static IResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "Saved request not found", "No saved request has this id.", "not-found");
}
