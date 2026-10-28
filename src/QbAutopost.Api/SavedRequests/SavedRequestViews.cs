using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QbAutopost.Core.Api;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// The JSON of the saved-request routes. T-1002 left enum naming to this task: statuses, outcomes and validation
/// states are kebab-case lower (<c>pending</c>, <c>in-progress</c>, <c>not-posted</c>), as spec 6 and 7 spell them.
/// These options are used by these routes only, so the camel-case enums of every older route are unchanged.
/// </summary>
public static class SavedRequestJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <summary>The wire name of an enum value, as <see cref="Options"/> writes it.</summary>
    public static string Name<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}

public sealed record SourceView(string Kind, string Id, string Name, string? Email);

public sealed record StampView(DateTime Utc, ActorView By);

public sealed record DuplicateView(bool Flagged, long? Of, IReadOnlyList<string> Reasons);

public sealed record DuplicateDetailView(
    bool Flagged, long? Of, IReadOnlyList<string> Reasons, ActorView? AcknowledgedBy, DateTime? AcknowledgedUtc);

/// <summary>Spec 6.4. <c>reason</c> is set only when the state is <c>unavailable</c>: why the check could not run.</summary>
public sealed record ValidationView(
    ValidationState State,
    DateTime ValidatedUtc,
    int VersionNumber,
    SyncPostFindingCounts? Counts,
    IReadOnlyList<SyncPostFinding> Validation,
    IReadOnlyList<string> Errors,
    DirectUnknownNames? UnknownNames,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

public sealed record ValidationSummaryView(ValidationState State, DateTime? ValidatedUtc);

public sealed record LastAttemptView(long Id, AttemptOutcome Outcome, int? HttpStatus, DateTime? FinishedUtc);

/// <summary>Spec 6.1: the answer to a save. n8n treats any 201 as success; the flags are for people.</summary>
public sealed record SavedView(
    long Id,
    string PublicRef,
    SavedRequestStatus Status,
    bool NeedsAttention,
    DuplicateView Duplicate,
    ValidationView? Validation,
    DateTime CreatedUtc);

/// <summary>Spec 6.2: one saved request.</summary>
public sealed record SavedRequestDetailView(
    long Id,
    string PublicRef,
    SavedRequestStatus Status,
    int RowVersion,
    SourceView Source,
    string? Reference,
    decimal? ControlTotal,
    int TransactionCount,
    int CurrentVersion,
    bool NeedsAttention,
    IReadOnlyList<string> ReadErrors,
    DuplicateDetailView Duplicate,
    ValidationView? Validation,
    JsonElement Payload,
    LastAttemptView? LastAttempt,
    string? PostId,
    string? DiscardReason,
    StampView Created,
    StampView Updated,
    IReadOnlyList<string> AllowedActions);

/// <summary>Spec 6.5: a list row, everything in 6.2 but the payload, the validation detail and the read errors.</summary>
public sealed record SavedRequestSummaryView(
    long Id,
    string PublicRef,
    SavedRequestStatus Status,
    int RowVersion,
    SourceView Source,
    string? Reference,
    decimal? ControlTotal,
    int TransactionCount,
    int CurrentVersion,
    bool NeedsAttention,
    DuplicateView Duplicate,
    ValidationSummaryView? Validation,
    string? PostId,
    StampView Created,
    StampView Updated,
    IReadOnlyList<string> AllowedActions);

public sealed record PageView<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>A history list (versions, attempts, events), newest first.</summary>
public sealed record ItemsView<T>(IReadOnlyList<T> Items);

public sealed record VersionSummaryView(int Number, StampView Created, string? Note, int ChangeCount, bool Readable);

public sealed record VersionView(
    int Number,
    JsonElement Payload,
    bool Readable,
    IReadOnlyList<string> ReadErrors,
    IReadOnlyList<PayloadChange> Changes,
    string? Note,
    StampView Created);

public record AttemptView(
    long Id,
    int VersionNumber,
    AttemptOutcome Outcome,
    int? HttpStatus,
    string? PostId,
    JsonNode? Counts,
    string? Error,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    ActorView StartedBy,
    ActorView? ResolvedBy,
    DateTime? ResolvedUtc,
    string? ResolutionNote);

/// <summary>One attempt with the full stored result: the <c>SyncPostResult</c> or the problem body, as returned.</summary>
public sealed record AttemptDetailView(
    long Id,
    int VersionNumber,
    AttemptOutcome Outcome,
    int? HttpStatus,
    string? PostId,
    JsonNode? Counts,
    string? Error,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    ActorView StartedBy,
    ActorView? ResolvedBy,
    DateTime? ResolvedUtc,
    string? ResolutionNote,
    JsonNode? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LedgerBatchView? LedgerBatch = null)
    : AttemptView(Id, VersionNumber, Outcome, HttpStatus, PostId, Counts, Error, StartedUtc, FinishedUtc, StartedBy,
        ResolvedBy, ResolvedUtc, ResolutionNote);

/// <summary>A row QuickBooks accepted, as the ledger recorded it: what an admin looks up in QuickBooks (spec 10.6).</summary>
public sealed record LedgerPostedRowView(int Index, string TxnId, string RequestId, string Kind, string Account, string? Payee, decimal Amount);

/// <summary>
/// Spec 10.6: the ledger batch behind an attempt's <c>postId</c>, read from the batch store that
/// <c>GET /api/v1/batches/{id}</c> reads, so an admin can check QuickBooks before resolving an unknown or partial post.
/// </summary>
public sealed record LedgerBatchView(
    string BatchId,
    JobStatus Status,
    DirectPostCounts Counts,
    DirectPostTotals Totals,
    string? Error,
    IReadOnlyList<LedgerPostedRowView> Posted);

public sealed record EventView(long Id, string Kind, JsonNode? Detail, DateTime AtUtc, ActorView Actor);

/// <summary>Spec 6: the dashboard header. Every status is present, zero included, so the header never guesses.</summary>
public sealed record StatsView(int Total, IReadOnlyDictionary<string, int> ByStatus, int NeedsAttention, int Duplicates);

/// <summary>Saved-requests spec 6.1, 6.2, 6.5, 5.3: records and history as the routes return them.</summary>
public static class SavedRequestViews
{
    public static SavedView Saved(SavedRequest r) =>
        new(r.Id, r.PublicRef, r.Status, r.NeedsAttention, Duplicate(r.DuplicateOfId, r.DuplicateReasons),
            Validation(r.Validation), r.CreatedUtc);

    /// <param name="readErrors">The current version's read errors (spec 6.1), which the record itself does not carry.</param>
    public static SavedRequestDetailView Detail(SavedRequest r, IReadOnlyList<string> readErrors, Caller c, ActorNames names) =>
        new(
            r.Id,
            r.PublicRef,
            r.Status,
            r.RowVersion,
            Source(r.Source, names),
            r.Reference,
            r.ControlTotal,
            r.TransactionCount,
            r.CurrentVersion,
            r.NeedsAttention,
            readErrors,
            new DuplicateDetailView(
                r.DuplicateReasons.Count > 0, r.DuplicateOfId, r.DuplicateReasons,
                r.DuplicateAcknowledgedBy is { } ack ? names.Of(ack) : null, r.DuplicateAcknowledgedUtc),
            Validation(r.Validation),
            r.CurrentPayload,
            r.LastAttempt is { } a ? new LastAttemptView(a.Id, a.Outcome, a.HttpStatus, a.FinishedUtc) : null,
            r.PostId,
            r.DiscardReason,
            new StampView(r.CreatedUtc, names.Of(r.CreatedBy)),
            new StampView(r.UpdatedUtc, names.Of(r.UpdatedBy)),
            AllowedActions(r.Status, c));

    public static SavedRequestSummaryView Summary(SavedRequestSummary s, Caller c, ActorNames names) =>
        new(
            s.Id,
            s.PublicRef,
            s.Status,
            s.RowVersion,
            Source(s.Source, names),
            s.Reference,
            s.ControlTotal,
            s.TransactionCount,
            s.CurrentVersion,
            s.NeedsAttention,
            Duplicate(s.DuplicateOfId, s.DuplicateReasons),
            s.ValidationState is { } state ? new ValidationSummaryView(state, s.ValidatedUtc) : null,
            s.PostId,
            new StampView(s.CreatedUtc, names.Of(s.CreatedBy)),
            new StampView(s.UpdatedUtc, names.Of(s.UpdatedBy)),
            AllowedActions(s.Status, c));

    public static VersionSummaryView VersionSummary(SavedRequestVersion v, ActorNames names) =>
        new(v.Number, new StampView(v.CreatedUtc, names.Of(v.CreatedBy)), v.Note, v.Changes.Count, v.Readable);

    public static VersionView Version(SavedRequestVersion v, ActorNames names) =>
        new(v.Number, v.Payload, v.Readable, v.ReadErrors, v.Changes, v.Note, new StampView(v.CreatedUtc, names.Of(v.CreatedBy)));

    public static AttemptView Attempt(PostAttempt a, ActorNames names) =>
        new(a.Id, a.VersionNumber, a.Outcome, a.HttpStatus, a.PostId, a.Counts, a.Error, a.StartedUtc, a.FinishedUtc,
            names.Of(a.StartedBy), a.ResolvedBy is { } by ? names.Of(by) : null, a.ResolvedUtc, a.ResolutionNote);

    public static AttemptDetailView AttemptDetail(PostAttempt a, ActorNames names, DirectPostResult? ledger = null) =>
        new(a.Id, a.VersionNumber, a.Outcome, a.HttpStatus, a.PostId, a.Counts, a.Error, a.StartedUtc, a.FinishedUtc,
            names.Of(a.StartedBy), a.ResolvedBy is { } by ? names.Of(by) : null, a.ResolvedUtc, a.ResolutionNote, a.Result,
            ledger is null ? null : LedgerBatch(ledger));

    public static LedgerBatchView LedgerBatch(DirectPostResult batch) =>
        new(
            batch.BatchId, batch.Status, batch.Counts, batch.Totals, batch.Error,
            batch.Posted.Select(r => new LedgerPostedRowView(
                r.Index, r.TxnId, r.RequestId, JsonNamingPolicy.CamelCase.ConvertName(r.Kind.ToString()), r.Account, r.Payee, r.Amount))
                .ToList());

    public static EventView Event(SavedRequestEvent e, ActorNames names) => new(e.Id, e.Kind, e.Detail, e.AtUtc, names.Of(e.Actor));

    public static StatsView Stats(SavedRequestStats s) =>
        new(
            s.Total,
            Enum.GetValues<SavedRequestStatus>().ToDictionary(SavedRequestJson.Name, status => s.ByStatus.GetValueOrDefault(status)),
            s.NeedsAttention,
            s.Duplicates);

    /// <summary>The actors a record names, for one batched name lookup.</summary>
    public static IEnumerable<string?> ActorsOf(SavedRequest r) =>
        [r.CreatedBy, r.UpdatedBy, r.DuplicateAcknowledgedBy, SourceActor(r.Source)];

    public static IEnumerable<string?> ActorsOf(SavedRequestSummary s) => [s.CreatedBy, s.UpdatedBy, SourceActor(s.Source)];

    /// <summary>Spec 5.2: <c>api-client:&lt;id&gt;</c> or <c>user:&lt;id&gt;</c>; anything else is shown as it is.</summary>
    private static SourceView Source(string source, ActorNames names)
    {
        if (source.StartsWith(ActorNames.ClientPrefix, StringComparison.Ordinal))
        {
            var id = source[ActorNames.ClientPrefix.Length..];
            return new SourceView("api-client", id, names.ClientName(id), null);
        }

        if (source.StartsWith(SavedRequestIntake.SourceUserPrefix, StringComparison.Ordinal))
        {
            var user = names.Of(source[SavedRequestIntake.SourceUserPrefix.Length..]);
            return new SourceView("user", user.Id, user.Name, user.Email);
        }

        return new SourceView(source, source, source, null);
    }

    /// <summary>The source as a history actor: a user source is the user's id, a client source is already an actor.</summary>
    private static string SourceActor(string source) =>
        source.StartsWith(SavedRequestIntake.SourceUserPrefix, StringComparison.Ordinal)
            ? source[SavedRequestIntake.SourceUserPrefix.Length..]
            : source;

    internal static DuplicateView Duplicate(long? of, IReadOnlyList<string> reasons) => new(reasons.Count > 0, of, reasons);

    internal static ValidationView? Validation(ValidationSnapshot? s) =>
        s is null
            ? null
            : new ValidationView(
                s.State, s.ValidatedUtc, s.VersionNumber, s.Counts, s.Validation, s.Errors, s.UnknownNames,
                s.State == ValidationState.Unavailable ? s.Errors.FirstOrDefault() : null);

    private static IReadOnlyList<string> AllowedActions(SavedRequestStatus status, Caller c) =>
        SavedRequestRules.AllowedActionNames(status, c.Scopes, c.Kind == CallerKind.User);
}
