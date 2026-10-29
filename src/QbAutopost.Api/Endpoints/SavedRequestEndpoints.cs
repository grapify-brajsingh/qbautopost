using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Api.Security;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Api;
using QbAutopost.Core.Store;
using Serilog.Context;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// Saved-requests spec 6: n8n saves a validate-and-post body here instead of posting it, and people read it in the
/// dashboard. Served under <c>/api/v1</c> only, behind the database gate (spec 10.7: <c>503 database-unavailable</c>
/// with <c>Retry-After: 30</c>, before any store call). The scope of each route is <see cref="ApiRoutes.ScopeFor"/>'s.
/// <para>
/// T-1012 maps the save and the reads; T-1013 adds edit, validate, discard and restore; T-1014 adds post.
/// </para>
/// <para>
/// Every answer is written with <see cref="SavedRequestJson.Options"/> (kebab-case enums), and every <c>{id}</c> goes
/// through <see cref="SavedRequestId.TryParse"/>: something that is not an id is <c>404</c>, like an unknown one.
/// </para>
/// </summary>
public static partial class SavedRequestEndpoints
{
    private const int DefaultPageSize = 25;
    private const string DefaultSort = "-created";

    /// <summary>Spec 7.1: the longest discard reason, after trimming.</summary>
    public const int MaxDiscardReasonLength = 500;

    private static readonly HashSet<string> Sorts = new(StringComparer.Ordinal)
    {
        "created", "-created", "updated", "-updated", "controlTotal", "-controlTotal",
    };

    private static readonly Dictionary<string, SavedRequestStatus> StatusNames =
        Enum.GetValues<SavedRequestStatus>().ToDictionary(s => SavedRequestJson.Name(s), StringComparer.Ordinal);

    /// <summary>Strict UTF-8: a body that is not valid UTF-8 cannot be stored "as received, as UTF-8" (spec 5.2).</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <param name="flavor">
    /// T-1109: the surface being mapped. T-1110 gave v2 its own save; T-1111 maps the v2 list, stats, detail and
    /// attempt detail to their own handlers (dashboard v2 spec 3, 9, 10); T-1112 gives the v2 changes the v2 detail
    /// and the v2 412 (spec 10 item 4.5); T-1113 does the rest.
    /// </param>
    public static IEndpointRouteBuilder MapSavedRequestEndpoints(this IEndpointRouteBuilder v1, ApiFlavor flavor = ApiFlavor.V1)
    {
        var group = v1.MapGroup("/saved-requests").RequireDatabase();
        group.AddEndpointFilter(SavedRequestLogContext);
        group.MapPost("/", (HttpContext http, SavedRequestIntake intake, CancellationToken ct) => SaveAsync(http, intake, flavor, ct));
        if (flavor == ApiFlavor.V2)
        {
            group.MapGet("/", ListV2Async);
            group.MapGet("/stats", StatsV2Async);
            group.MapGet("/{id}", GetV2Async);
            group.MapGet("/{id}/attempts/{attemptId}", AttemptV2Async);
        }
        else
        {
            group.MapGet("/", ListAsync);
            group.MapGet("/stats", StatsAsync);
            group.MapGet("/{id}", GetAsync);
            group.MapGet("/{id}/attempts/{attemptId}", AttemptAsync);
        }

        // T-1112: the changes share one handler per route; the flavor picks the reply (the v2 detail, the v2 412).
        group.MapPut(
            "/{id}",
            (HttpContext http, string id, SavedRequestIntake intake, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct) =>
                EditAsync(http, id, intake, store, actors, flavor, ct));
        group.MapPost(
            "/{id}/validate",
            (HttpContext http, string id, SavedRequestIntake intake, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct) =>
                ValidateAsync(http, id, intake, store, actors, flavor, ct));
        group.MapPost(
            "/{id}/discard",
            (HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, IClock clock, CancellationToken ct) =>
                DiscardAsync(http, id, store, actors, clock, flavor, ct));
        group.MapPost(
            "/{id}/restore",
            (HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, IClock clock, CancellationToken ct) =>
                RestoreAsync(http, id, store, actors, clock, flavor, ct));
        group.MapPost(
            "/{id}/post",
            (HttpContext http, string id, SavedRequestPoster poster, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct) =>
                PostAsync(http, id, poster, store, actors, flavor, ct));
        group.MapPost(
            "/{id}/resolve",
            (HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, IClock clock, CancellationToken ct) =>
                ResolveAsync(http, id, store, actors, clock, flavor, ct));
        group.MapGet("/{id}/original", OriginalAsync);
        group.MapGet("/{id}/versions", VersionsAsync);
        group.MapGet("/{id}/versions/{n}", VersionAsync);
        group.MapGet("/{id}/attempts", AttemptsAsync);
        group.MapGet("/{id}/events", EventsAsync);
        return v1;
    }

    /// <summary>
    /// Spec 12: every line written while a route with an <c>{id}</c> runs carries <c>savedRequestId</c>, beside the
    /// <c>requestId</c> and <c>clientId</c> the middleware pushed. An id that is not one adds nothing: the answer is 404.
    /// </summary>
    private static async ValueTask<object?> SavedRequestLogContext(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var raw = context.HttpContext.Request.RouteValues["id"]?.ToString();
        if (raw is null || !SavedRequestId.TryParse(raw, out var id))
        {
            return await next(context);
        }

        using var scope = LogContext.PushProperty(SavedRequestPoster.SavedRequestIdField, id);
        return await next(context);
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
    private static async Task<IResult> SaveAsync(HttpContext http, SavedRequestIntake intake, ApiFlavor flavor, CancellationToken ct)
    {
        var (raw, failure) = await ReadBodyAsync(http, ct);
        if (failure is not null)
        {
            return failure;
        }

        if (!SavedRequestIntake.TryParseObject(raw!, out var payload))
        {
            return NotAJsonObject("The body must be one JSON object: the validate-and-post body (validate-and-post §4).");
        }

        if (SavedRequestIntake.NotStorableReason(payload) is { } notStorable)
        {
            return NotStorable(notStorable);
        }

        var key = http.Request.Headers[IdempotencyMiddleware.KeyHeader].ToString().Trim();
        if (key.Length > IdempotencyMiddleware.MaxKeyLength)
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Idempotency-Key too long",
                $"An Idempotency-Key may be at most {IdempotencyMiddleware.MaxKeyLength} characters.", "invalid-request");
        }

        var caller = CallerMiddleware.CallerOf(http)!;
        if (flavor == ApiFlavor.V2 && UploadField.Read(payload) is { Kind: not UploadFieldKind.Absent } upload)
        {
            return await SaveUploadAsync(http, intake, raw!, payload, upload, key.Length == 0 ? null : key, caller, ct);
        }

        var result = await intake.SaveAsync(raw!, key.Length == 0 ? null : key, caller, ct, flavor);
        var record = result.Record;
        switch (result.Kind)
        {
            case CreateKind.KeyReused:
                return KeyReused(record);
            case CreateKind.Replayed:
                http.Response.Headers[IdempotencyMiddleware.ReplayedHeader] = "true";
                return flavor == ApiFlavor.V2 ? Json(SavedRequestViewsV2.Saved(record)) : Json(SavedRequestViews.Saved(record));
            case CreateKind.PushIdReused when flavor == ApiFlavor.V2:
                return PushIdReused(record);
            case CreateKind.PushIdReused:
                // v1 never sets SaveMetadata.PushId, so the store cannot report this here. It is not mapped to a
                // v1 answer on purpose: any v1 body for it would be new behaviour. v2 maps it to push-id-reused.
                throw new UnreachableException("A v1 save carries no pushId, so it cannot be push-id-reused.");
            default:
                http.Response.Headers.Location = Location(flavor, record.Id);
                return flavor == ApiFlavor.V2
                    ? Results.Json(
                        SavedRequestViewsV2.Saved(record), SavedRequestJson.Options, statusCode: StatusCodes.Status201Created)
                    : Results.Json(
                        SavedRequestViews.Saved(record), SavedRequestJson.Options, statusCode: StatusCodes.Status201Created);
        }
    }

    /// <summary>10.1: the Idempotency-Key is held by <paramref name="holder"/>, saved from a different body.</summary>
    private static IResult KeyReused(SavedRequest holder) =>
        Problem(
            StatusCodes.Status409Conflict, "Idempotency-Key already used",
            $"This Idempotency-Key was already used for {holder.PublicRef} with a different body. Nothing was saved. "
            + "Send a new key for a new request.",
            "idempotency-key-reused");

    /// <summary>
    /// Dashboard v2 spec 5.2 (Q-94): the body's <c>pushId</c> is held by <paramref name="holder"/>, saved from a
    /// different body. Nothing was stored; the problem names the holder so a person can find it.
    /// </summary>
    private static IResult PushIdReused(SavedRequest holder) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "pushId already used",
            detail: $"This pushId was already used for {holder.PublicRef} with a different body. Nothing was saved. "
                + "Send a new pushId for a new request.",
            type: "push-id-reused",
            extensions: new Dictionary<string, object?>
            {
                ["savedRequestId"] = holder.Id,
                ["publicRef"] = holder.PublicRef,
            });

    /// <summary>
    /// The <c>Location</c> of a saved request on the surface it was saved through: <c>/api/v1/saved-requests/{id}</c>
    /// for v1, as before, and <c>/api/v2/saved-requests/{id}</c> for v2 (T-1109).
    /// </summary>
    public static string Location(ApiFlavor flavor, long id) =>
        $"{(flavor == ApiFlavor.V2 ? ApiRoutes.V2Prefix : ApiRoutes.V1Prefix)}/saved-requests/{id.ToString(CultureInfo.InvariantCulture)}";

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

    /// <summary>Dashboard v2 spec 9.2: v1's parameters plus the v2 ones (<see cref="V2QueryParser"/>); rows are v2 rows.</summary>
    private static async Task<IResult> ListV2Async(
        HttpContext http, ISavedRequestV2Store store, ActorDirectory actors, IOptions<AppSettings> settings, CancellationToken ct)
    {
        if (!V2QueryParser.TryParse(http.Request.Query, settings.Value.SavedRequests.MaxListPageSize, out var query, out var error))
        {
            return BadRequest("Invalid query", error!);
        }

        var page = await store.ListV2Async(query!, ct);
        var names = await actors.ResolveAsync(page.Items.SelectMany(SavedRequestViewsV2.ActorsOf), ct);
        var caller = CallerMiddleware.CallerOf(http)!;
        var items = page.Items.Select(s => SavedRequestViewsV2.Summary(s, caller, names)).ToList();
        return Json(new PageView<SavedRequestSummaryV2View>(items, page.Page, page.PageSize, page.Total));
    }

    /// <summary>Dashboard v2 spec 9.3: every flag counted among pending requests and among all, on the v2 columns.</summary>
    private static async Task<IResult> StatsV2Async(ISavedRequestV2Store store, CancellationToken ct) =>
        Json(SavedRequestViewsV2.Stats(await store.StatsV2Async(ct)));

    // ---- one request ------------------------------------------------------------------------------------------------

    /// <summary>Spec 6.2. The <c>ETag</c> is the row version; <c>allowedActions</c> follow the status and the caller's scopes.</summary>
    private static async Task<IResult> GetAsync(
        HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        if (await FindAsync(store, id, ct) is not { } record)
        {
            return NotFound();
        }

        return await DetailAsync(http, record, store, actors, ct);
    }

    /// <summary>
    /// Dashboard v2 spec 9.1, 6.4, 10 items 4.1 and 4.9: the v2 detail, with its <c>ETag</c>. Read through
    /// <see cref="ISavedRequestV2Store.GetV2Async"/> (ruling R7), the only read that fills the post summary and history
    /// count. <c>ledgerBatch</c> is read only for a partial or unknown request whose last attempt has a <c>postId</c>.
    /// </summary>
    private static async Task<IResult> GetV2Async(
        HttpContext http, string id, ISavedRequestStore store, ISavedRequestV2Store v2, ActorDirectory actors,
        ApiBatchStore batches, ILoggerFactory loggers, CancellationToken ct)
    {
        return SavedRequestId.TryParse(id, out var requestId)
            ? await DetailV2Async(http, requestId, store, v2, actors, batches, loggers, ct)
            : NotFound();
    }

    /// <summary>The v2 detail of <paramref name="requestId"/> with its <c>ETag</c>, or <c>404</c> when there is no such request.</summary>
    private static async Task<IResult> DetailV2Async(
        HttpContext http, long requestId, ISavedRequestStore store, ISavedRequestV2Store v2, ActorDirectory actors,
        ApiBatchStore batches, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await v2.GetV2Async(requestId, ct) is not { } record)
        {
            return NotFound();
        }

        var version = await store.VersionAsync(record.Id, record.CurrentVersion, ct);
        var readErrors = SavedRequestViewsV2.ReadErrors(
            record.Metadata, version?.ReadErrors ?? [], version?.Payload ?? record.CurrentPayload);
        var others = await v2.OtherUnpostedPushesAsync(record.Id, ct);
        var ledger = record.Status is SavedRequestStatus.Partial or SavedRequestStatus.Unknown
                     && record.LastAttempt?.PostId is { } postId
            ? LoadDetailLedgerBatch(batches, postId, record.Id, loggers)
            : null;
        var names = await actors.ResolveAsync(SavedRequestViewsV2.ActorsOf(record), ct);
        http.Response.Headers.ETag = $"\"{record.RowVersion.ToString(CultureInfo.InvariantCulture)}\"";
        return Json(SavedRequestViewsV2.Detail(record, readErrors, others, ledger, CallerMiddleware.CallerOf(http)!, names));
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

    /// <summary>
    /// Spec 6.3, 10.6: one attempt with its stored result. When it has a <c>postId</c> the ledger batch behind it is
    /// added (<c>ledgerBatch</c>), read from the batch store <c>GET /batches/{id}</c> reads. A batch that is missing or
    /// cannot be read is left out and logged: it must never fail the read, because the admin needs the rest of the
    /// attempt precisely when the batch is in doubt.
    /// </summary>
    private static async Task<IResult> AttemptAsync(
        string id, string attemptId, ISavedRequestStore store, ActorDirectory actors, ApiBatchStore batches,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!long.TryParse(attemptId, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1
            || !SavedRequestId.TryParse(id, out var requestId)
            || await store.AttemptAsync(requestId, attempt, ct) is not { } found)
        {
            return NotFound();
        }

        using var scope = LogContext.PushProperty(SavedRequestPoster.AttemptIdField, found.Id);
        var ledger = found.PostId is { } postId ? LoadLedgerBatch(batches, postId, requestId, loggers) : null;
        var names = await actors.ResolveAsync([found.StartedBy, found.ResolvedBy], ct);
        return Json(SavedRequestViews.AttemptDetail(found, names, ledger));
    }

    /// <summary>
    /// Dashboard v2 spec 10 items 4.8 and 9.3: the v1 attempt detail, with <c>externalId</c> on each ledger row. The
    /// ledger batch is read and left out exactly as v1 does (<see cref="AttemptAsync"/>).
    /// </summary>
    private static async Task<IResult> AttemptV2Async(
        string id, string attemptId, ISavedRequestStore store, ActorDirectory actors, ApiBatchStore batches,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!long.TryParse(attemptId, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt) || attempt < 1
            || !SavedRequestId.TryParse(id, out var requestId)
            || await store.AttemptAsync(requestId, attempt, ct) is not { } found)
        {
            return NotFound();
        }

        using var scope = LogContext.PushProperty(SavedRequestPoster.AttemptIdField, found.Id);
        var ledger = found.PostId is { } postId ? LoadLedgerBatch(batches, postId, requestId, loggers) : null;
        var names = await actors.ResolveAsync([found.StartedBy, found.ResolvedBy], ct);
        return Json(SavedRequestViewsV2.AttemptDetail(found, names, ledger));
    }

    /// <summary>
    /// The ledger batch of a v2 detail. Like <see cref="LoadLedgerBatch"/>, a batch that is missing or cannot be read
    /// is left out and logged, and never fails the read.
    /// </summary>
    private static DirectPostResult? LoadDetailLedgerBatch(ApiBatchStore batches, string postId, long requestId, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger(typeof(SavedRequestEndpoints));
        using var scope = LogContext.PushProperty("postId", postId);
        var batch = TryLoadBatch(batches, postId, out var error);
        if (error is not null)
        {
            log.LogWarning(
                "Saved request {SavedRequestId}: ledger batch {PostId} could not be read ({Error}); left out of the detail",
                requestId, postId, error.GetType().Name);
        }
        else if (batch is null)
        {
            log.LogWarning("Saved request {SavedRequestId}: ledger batch {PostId} is missing or unreadable; left out of the detail", requestId, postId);
        }

        return batch;
    }

    private static DirectPostResult? LoadLedgerBatch(ApiBatchStore batches, string postId, long requestId, ILoggerFactory loggers)
    {
        var log = loggers.CreateLogger(typeof(SavedRequestEndpoints));
        using var scope = LogContext.PushProperty("postId", postId);
        var batch = TryLoadBatch(batches, postId, out var error);
        if (error is not null)
        {
            log.LogWarning(
                "Saved request {SavedRequestId}: ledger batch {PostId} could not be read ({Error}); left out of the attempt",
                requestId, postId, error.GetType().Name);
        }
        else if (batch is null)
        {
            log.LogWarning("Saved request {SavedRequestId}: ledger batch {PostId} is missing or unreadable; left out of the attempt", requestId, postId);
        }

        return batch;
    }

    /// <summary>
    /// The batch behind <paramref name="postId"/>, or null when it is missing or unreadable. A failure to read it that
    /// a caller must survive (a bad id, I/O, access) is returned in <paramref name="error"/>, never thrown; each caller
    /// logs it in its own words.
    /// </summary>
    private static DirectPostResult? TryLoadBatch(ApiBatchStore batches, string postId, out Exception? error)
    {
        error = null;
        try
        {
            return batches.Load(postId);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            error = ex;
            return null;
        }
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

    // ---- edit, validate, discard, restore ---------------------------------------------------------------------------

    /// <summary>
    /// Spec 6.3. <c>If-Match</c> is required (<c>428</c>); a stale one is <c>412 edited-by-someone-else</c> naming the
    /// current <c>rowVersion</c> and who last changed the request; anything but <c>pending</c> is <c>409 not-editable</c>.
    /// An unreadable payload is stored and flagged, like a save. An identical payload answers <c>200</c> and writes nothing.
    /// Nothing recomputes the control total (CLAUDE.md rule 3): it is whatever the editor sent.
    /// </summary>
    private static async Task<IResult> EditAsync(
        HttpContext http, string id, SavedRequestIntake intake, ISavedRequestStore store, ActorDirectory actors,
        ApiFlavor flavor, CancellationToken ct)
    {
        if (IfMatchOf(http) is not { } expected)
        {
            return Problem(
                StatusCodes.Status428PreconditionRequired, "If-Match required",
                "Send the request's ETag in If-Match (GET /saved-requests/{id} returns it).", "precondition-required");
        }

        var (raw, failure) = await ReadBodyAsync(http, ct);
        if (failure is not null)
        {
            return failure;
        }

        if (!SavedRequestIntake.TryParseObject(raw!, out var body)
            || !body.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return NotAJsonObject("The body must be { \"payload\": { ...the validate-and-post body... }, \"note\": \"...\" }.");
        }

        if (SavedRequestIntake.NotStorableReason(payload) is { } notStorable)
        {
            return NotStorable(notStorable);
        }

        string? note = null;
        if (body.TryGetProperty("note", out var noteElement) && noteElement.ValueKind != JsonValueKind.Null)
        {
            if (noteElement.ValueKind != JsonValueKind.String)
            {
                return BadRequest("Invalid note", "note must be text.");
            }

            note = string.IsNullOrWhiteSpace(noteElement.GetString()) ? null : noteElement.GetString()!.Trim();
        }

        if (!SavedRequestId.TryParse(id, out var requestId))
        {
            return NotFound();
        }

        var result = await intake.EditAsync(requestId, expected, payload, note, CallerMiddleware.CallerOf(http)!, ct, flavor);
        return await MutationAnswerAsync(http, result, "not-editable", "Not editable", "edited", store, actors, flavor, ct);
    }

    /// <summary>Spec 6.4: re-runs validation and the duplicate rules for the current version. Pending only.</summary>
    private static async Task<IResult> ValidateAsync(
        HttpContext http, string id, SavedRequestIntake intake, ISavedRequestStore store, ActorDirectory actors,
        ApiFlavor flavor, CancellationToken ct)
    {
        if (!SavedRequestId.TryParse(id, out var requestId))
        {
            return NotFound();
        }

        var result = await intake.RevalidateAsync(requestId, CallerMiddleware.CallerOf(http)!, ct);
        return await MutationAnswerAsync(http, result, "not-editable", "Not editable", "validated", store, actors, flavor, ct);
    }

    /// <summary>Spec 7.1: pending to discarded, with a reason of 1 to 500 characters after trimming. <c>If-Match</c> is honoured when sent.</summary>
    private static async Task<IResult> DiscardAsync(
        HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, IClock clock, ApiFlavor flavor,
        CancellationToken ct)
    {
        var (raw, failure) = await ReadBodyAsync(http, ct);
        if (failure is not null)
        {
            return failure;
        }

        string? reason = null;
        if (SavedRequestIntake.TryParseObject(raw!, out var body)
            && body.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
        {
            reason = reasonElement.GetString()!.Trim();
        }

        if (reason is null or { Length: 0 } || reason.Length > MaxDiscardReasonLength)
        {
            return BadRequest("Reason required", $"Send {{ \"reason\": \"...\" }}, 1 to {MaxDiscardReasonLength} characters.");
        }

        if (!SavedRequestId.TryParse(id, out var requestId))
        {
            return NotFound();
        }

        var result = await store.ChangeStatusAsync(
            requestId, IfMatchOf(http), SavedRequestStatus.Pending, SavedRequestStatus.Discarded,
            CallerMiddleware.CallerOf(http)!.Actor, clock.UtcNow, SavedRequestEvents.Discarded,
            new JsonObject { ["reason"] = reason }, ct);
        return await MutationAnswerAsync(http, result, "not-discardable", "Not discardable", "discarded", store, actors, flavor, ct);
    }

    /// <summary>Spec 7.1: discarded back to pending, for an admin (the scope is <see cref="ApiRoutes.ScopeFor"/>'s). Clears the reason.</summary>
    private static async Task<IResult> RestoreAsync(
        HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, IClock clock, ApiFlavor flavor,
        CancellationToken ct)
    {
        if (!SavedRequestId.TryParse(id, out var requestId))
        {
            return NotFound();
        }

        var result = await store.ChangeStatusAsync(
            requestId, IfMatchOf(http), SavedRequestStatus.Discarded, SavedRequestStatus.Pending,
            CallerMiddleware.CallerOf(http)!.Actor, clock.UtcNow, SavedRequestEvents.Restored, null, ct);
        return await MutationAnswerAsync(http, result, "not-restorable", "Not restorable", "restored", store, actors, flavor, ct);
    }

    // ---- resolve ----------------------------------------------------------------------------------------------------

    /// <summary>Spec 10.6: the longest resolution note, after trimming (the same bound as a discard reason).</summary>
    public const int MaxResolveNoteLength = 500;

    /// <summary>
    /// Spec 10.6, admin only (the scope is <see cref="ApiRoutes.ScopeFor"/>'s): settles an <c>unknown</c> or
    /// <c>partial</c> request after a person has checked QuickBooks. Body <c>{ "outcome": "posted" | "not-posted",
    /// "note": "..." }</c>, the note 1 to 500 characters after trimming. Any other status, and <c>not-posted</c> for a
    /// partial post (rows have landed; removing them is undo's job), is <c>409 not-resolvable</c>. <c>If-Match</c> is
    /// honoured when sent. Nothing is sent to QuickBooks.
    /// </summary>
    private static async Task<IResult> ResolveAsync(
        HttpContext http, string id, ISavedRequestStore store, ActorDirectory actors, IClock clock, ApiFlavor flavor,
        CancellationToken ct)
    {
        var (raw, failure) = await ReadBodyAsync(http, ct);
        if (failure is not null)
        {
            return failure;
        }

        ResolveOutcome? outcome = null;
        string? note = null;
        if (SavedRequestIntake.TryParseObject(raw!, out var body))
        {
            if (body.TryGetProperty("outcome", out var outcomeElement) && outcomeElement.ValueKind == JsonValueKind.String)
            {
                outcome = outcomeElement.GetString() switch
                {
                    "posted" => ResolveOutcome.Posted,
                    "not-posted" => ResolveOutcome.NotPosted,
                    _ => null,
                };
            }

            if (body.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String)
            {
                note = noteElement.GetString()!.Trim();
            }
        }

        if (outcome is null || note is null or { Length: 0 } || note.Length > MaxResolveNoteLength)
        {
            return BadRequest(
                "Outcome and note required",
                $"Send {{ \"outcome\": \"posted\" | \"not-posted\", \"note\": \"...\" }}, the note 1 to {MaxResolveNoteLength} characters. "
                + "Nothing was resolved.");
        }

        if (!SavedRequestId.TryParse(id, out var requestId))
        {
            return NotFound();
        }

        // The store's ResolveAsync takes no row version, so a sent If-Match is checked here. The status test that
        // decides the move is still the store's, in the same transaction as the write.
        if (IfMatchOf(http) is { } expected)
        {
            if (await store.GetAsync(requestId, ct) is not { } current)
            {
                return NotFound();
            }

            if (current.RowVersion != expected)
            {
                return await MutationAnswerAsync(
                    http, new MutationResult(MutationKind.Stale, current), "not-resolvable", "Not resolvable", "resolved", store, actors, flavor, ct);
            }
        }

        var result = await store.ResolveAsync(requestId, outcome.Value, note, CallerMiddleware.CallerOf(http)!.Actor, clock.UtcNow, ct);
        return await MutationAnswerAsync(http, result, "not-resolvable", "Not resolvable", "resolved", store, actors, flavor, ct);
    }

    // ---- post -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Spec 7.2, 9, 10.2-10.5: posts the current version through validate-and-post's engine
    /// (<see cref="SavedRequestPoster"/>). The body is optional: nothing, or <c>{ "confirmDuplicate": true }</c> for a
    /// request flagged as a possible duplicate. <c>If-Match</c> is honoured when sent (412 when stale).
    /// <para>
    /// When the engine ran, the answer is validate-and-post's own status and body (<see cref="SyncPostResponses"/>)
    /// plus <c>savedRequestId</c>, <c>attemptId</c> and <c>savedRequestStatus</c>. Otherwise it is 404, 412, or 409
    /// <c>already-posting</c> (with the running attempt's id), <c>already-posted</c> (with the post id),
    /// <c>not-postable</c> or <c>duplicate-unconfirmed</c> (naming the other request and its status), and nothing was
    /// claimed or sent.
    /// </para>
    /// <para>
    /// Dashboard v2 (spec 6.2, 10 item 4.4): v2 honours <c>post: false</c> (the row is listed under <c>skipped</c> as
    /// <c>not-to-post</c> and never sent), answers <c>400 nothing-to-post</c> with no attempt when every row is off, and
    /// its two 409s also carry <c>attemptId</c>, <c>startedBy {id,name}</c> and <c>startedUtc</c>.
    /// </para>
    /// </summary>
    private static async Task<IResult> PostAsync(
        HttpContext http, string id, SavedRequestPoster poster, ISavedRequestStore store, ActorDirectory actors,
        ApiFlavor flavor, CancellationToken ct)
    {
        var (raw, failure) = await ReadBodyAsync(http, ct);
        if (failure is not null)
        {
            return failure;
        }

        if (ConfirmDuplicateOf(raw!) is not { } confirmDuplicate)
        {
            return BadRequest(
                "Invalid body", "Send no body, or { \"confirmDuplicate\": true } to post a possible duplicate. Nothing was posted.");
        }

        if (!SavedRequestId.TryParse(id, out var requestId))
        {
            return NotFound();
        }

        var outcome = await poster.PostAsync(
            requestId, IfMatchOf(http), confirmDuplicate, CallerMiddleware.CallerOf(http)!, ct, flavor);
        var record = outcome.Record;
        switch (outcome.Kind)
        {
            case SavedPostKind.NotFound:
                return NotFound();
            case SavedPostKind.NothingToPost:
                return Problem(
                    StatusCodes.Status400BadRequest, "Nothing to post",
                    $"nothing-to-post: every row is marked post: false; nothing would be sent. {record!.PublicRef} is "
                    + "still pending; nothing was claimed or sent.",
                    SkipReasons.NothingToPost);
            case SavedPostKind.Stale:
                return await MutationAnswerAsync(
                    http, new MutationResult(MutationKind.Stale, record), "not-postable", "Not postable", "posted", store, actors, flavor, ct);
            case SavedPostKind.AlreadyPosting:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Already posting",
                    type: "already-posting",
                    detail: $"{record!.PublicRef} is being posted by attempt {outcome.AttemptId}. Nothing was sent by this "
                            + "request. Read the request until it is no longer posting.",
                    extensions: flavor == ApiFlavor.V2
                        ? await PostingByAsync(record, outcome.AttemptId, store, actors, ct)
                        : new Dictionary<string, object?> { [SavedRequestPoster.AttemptIdField] = outcome.AttemptId });
            case SavedPostKind.AlreadyPosted:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Already posted",
                    type: "already-posted",
                    detail: $"{record!.PublicRef} is already in QuickBooks as post {record.PostId}. Nothing was sent. "
                            + "A correction is an undo of that post and a new saved request.",
                    extensions: flavor == ApiFlavor.V2
                        ? await PostedByAsync(record, store, actors, ct)
                        : new Dictionary<string, object?> { ["postId"] = record.PostId });
            case SavedPostKind.NotPostable:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Not postable",
                    type: "not-postable",
                    detail: $"{record!.PublicRef} is {SavedRequestJson.Name(record.Status)}, so it cannot be posted. Nothing was sent.",
                    extensions: new Dictionary<string, object?>
                    {
                        [SavedRequestPoster.SavedRequestStatusField] = SavedRequestJson.Name(record.Status),
                    });
            case SavedPostKind.DuplicateUnconfirmed:
                var other = record!.DuplicateOfId is { } of ? await store.GetAsync(of, ct) : null;
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Possible duplicate",
                    type: "duplicate-unconfirmed",
                    detail: $"{record.PublicRef} may duplicate {other?.PublicRef ?? "another saved request"}"
                            + (other is null ? "" : $", which is {SavedRequestJson.Name(other.Status)}")
                            + ". Nothing was posted. Send { \"confirmDuplicate\": true } to post it anyway.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["duplicate"] = new Dictionary<string, object?>
                        {
                            ["of"] = record.DuplicateOfId,
                            ["status"] = other is null ? null : SavedRequestJson.Name(other.Status),
                            ["reasons"] = record.DuplicateReasons,
                        },
                    });
        }

        // Ran or Unreadable: an attempt exists, and the answer says which and where the request now stands.
        if (outcome.Problem is { } problem)
        {
            return TypedResults.Problem(problem);
        }

        return SyncPostResponses.ToResult(
            outcome.Post!, http, SavedRequestPoster.ExtraFields(record!.Id, outcome.AttemptId!.Value, outcome.Status!.Value));
    }

    /// <summary>Dashboard v2 10 item 4.4: the v2 <c>already-posting</c> names the running attempt, who started it and when.</summary>
    private static async Task<Dictionary<string, object?>> PostingByAsync(
        SavedRequest record, long? attemptId, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        var running = attemptId is { } value ? await store.AttemptAsync(record.Id, value, ct) : null;
        var extras = await StartedByAsync(running, actors, ct);
        extras[SavedRequestPoster.AttemptIdField] = attemptId;
        return extras;
    }

    /// <summary>
    /// Dashboard v2 10 item 4.4: the v2 <c>already-posted</c> names the post and the attempt that posted it. A request
    /// settled by resolve has no posted attempt, so <c>attemptId</c>, <c>startedBy</c> and <c>startedUtc</c> are null.
    /// </summary>
    private static async Task<Dictionary<string, object?>> PostedByAsync(
        SavedRequest record, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        var posted = (await store.AttemptsAsync(record.Id, ct))
            .Where(a => a.Outcome == AttemptOutcome.Posted)
            .MaxBy(a => a.Id);
        var extras = await StartedByAsync(posted, actors, ct);
        extras["postId"] = record.PostId;
        return extras;
    }

    /// <summary>Dashboard v2 10 item 4.4: <c>attemptId</c>, <c>startedBy {id,name}</c> and <c>startedUtc</c> of an attempt, or nulls.</summary>
    private static async Task<Dictionary<string, object?>> StartedByAsync(
        PostAttempt? attempt, ActorDirectory actors, CancellationToken ct)
    {
        if (attempt is null)
        {
            return new Dictionary<string, object?>
            {
                [SavedRequestPoster.AttemptIdField] = null,
                ["startedBy"] = null,
                ["startedUtc"] = null,
            };
        }

        var by = (await actors.ResolveAsync([attempt.StartedBy], ct)).Of(attempt.StartedBy);
        return new Dictionary<string, object?>
        {
            [SavedRequestPoster.AttemptIdField] = attempt.Id,
            ["startedBy"] = new { id = by.Id, name = by.Name },
            ["startedUtc"] = attempt.StartedUtc,
        };
    }

    /// <summary>The post body's <c>confirmDuplicate</c>: false when there is no body, null when the body is anything else.</summary>
    private static bool? ConfirmDuplicateOf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!SavedRequestIntake.TryParseObject(raw, out var body))
        {
            return null;
        }

        if (!body.TryGetProperty("confirmDuplicate", out var confirm) || confirm.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return confirm.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>
    /// The one answer every change gives. Not found is <c>404</c>; a stale row version is <c>412</c> with the current
    /// <c>rowVersion</c> and the last person to change it; a status the change does not apply to is <c>409</c> naming the
    /// status; anything else is the record, with its new <c>ETag</c> and <c>allowedActions</c> for the caller.
    /// <para>
    /// Dashboard v2 (spec 3, 10 item 4.5): the v2 412 also carries <c>updatedUtc</c>, and the record is the v2 detail,
    /// the same shape as <c>GET /api/v2/saved-requests/{id}</c>.
    /// </para>
    /// </summary>
    private static async Task<IResult> MutationAnswerAsync(
        HttpContext http, MutationResult result, string conflictType, string conflictTitle, string verb,
        ISavedRequestStore store, ActorDirectory actors, ApiFlavor flavor, CancellationToken ct)
    {
        var record = result.Record;
        switch (result.Kind)
        {
            case MutationKind.NotFound:
                return NotFound();
            case MutationKind.Stale:
                var by = (await actors.ResolveAsync([record!.UpdatedBy], ct)).Of(record.UpdatedBy);
                var extras = new Dictionary<string, object?>
                {
                    ["rowVersion"] = record.RowVersion,
                    ["updatedBy"] = new { id = by.Id, name = by.Name },
                };
                if (flavor == ApiFlavor.V2)
                {
                    extras["updatedUtc"] = record.UpdatedUtc;
                }

                return Results.Problem(
                    statusCode: StatusCodes.Status412PreconditionFailed,
                    title: "Edited by someone else",
                    detail: $"{record.PublicRef} was changed by {by.Name}. Reload it and try again. Nothing was {verb}.",
                    type: "edited-by-someone-else",
                    extensions: extras);
            case MutationKind.WrongStatus:
                return Problem(
                    StatusCodes.Status409Conflict, conflictTitle,
                    $"{record!.PublicRef} is {SavedRequestJson.Name(record.Status)}, so this is not allowed. Nothing was {verb}.",
                    conflictType);
            default:
                return flavor == ApiFlavor.V2
                    ? await DetailV2Async(
                        http, record!.Id, store, http.RequestServices.GetRequiredService<ISavedRequestV2Store>(), actors,
                        http.RequestServices.GetRequiredService<ApiBatchStore>(),
                        http.RequestServices.GetRequiredService<ILoggerFactory>(), ct)
                    : await DetailAsync(http, record!, store, actors, ct);
        }
    }

    // ---- shared steps -----------------------------------------------------------------------------------------------

    /// <summary>Spec 6.2: the record with its <c>ETag</c> and the caller's <c>allowedActions</c>.</summary>
    private static async Task<IResult> DetailAsync(
        HttpContext http, SavedRequest record, ISavedRequestStore store, ActorDirectory actors, CancellationToken ct)
    {
        var version = await store.VersionAsync(record.Id, record.CurrentVersion, ct);
        var names = await actors.ResolveAsync(SavedRequestViews.ActorsOf(record), ct);
        http.Response.Headers.ETag = $"\"{record.RowVersion.ToString(CultureInfo.InvariantCulture)}\"";
        return Json(SavedRequestViews.Detail(record, version?.ReadErrors ?? [], CallerMiddleware.CallerOf(http)!, names));
    }

    /// <summary>
    /// The request body as UTF-8 text, or the answer to give instead: invalid UTF-8 is <c>400 not-a-json-object</c> and a
    /// chunked body over the limit is <c>413</c> (a declared length is <c>RequestSizeMiddleware</c>'s, before this runs).
    /// </summary>
    private static async Task<(string? Raw, IResult? Failure)> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        try
        {
            http.Request.EnableBuffering();
            http.Request.Body.Position = 0;
            using var reader = new StreamReader(http.Request.Body, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            return (await reader.ReadToEndAsync(ct), null);
        }
        catch (DecoderFallbackException)
        {
            return (null, NotAJsonObject("The body is not valid UTF-8."));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // A chunked body declares no length, so only the server can stop it mid-flight (RequestSizeMiddleware).
            return (null, Problem(StatusCodes.Status413PayloadTooLarge, "Request body too large", ex.Message, "payload-too-large"));
        }
    }

    /// <summary>
    /// The row version in <c>If-Match</c>, or null when the header is absent. Something that is not a whole number
    /// can never be a row version, so it becomes -1 and the store answers it as stale.
    /// </summary>
    private static int? IfMatchOf(HttpContext http)
    {
        var header = http.Request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            return null;
        }

        if (header.StartsWith("W/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        return int.TryParse(header.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var version) ? version : -1;
    }

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

    /// <summary>R33: valid JSON the database cannot hold. Refused, not stored, and never answered as an outage.</summary>
    private static IResult NotStorable(string reason) =>
        Problem(
            StatusCodes.Status400BadRequest, "Payload not storable",
            $"The payload is valid JSON, but {reason}. Remove it and send again. Nothing was saved.", "payload-not-storable");

    private static IResult BadRequest(string title, string detail) =>
        Problem(StatusCodes.Status400BadRequest, title, detail, "invalid-request");

    private static IResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "Saved request not found", "No saved request has this id.", "not-found");
}
