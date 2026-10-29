using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QbAutopost.Core.Api;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.SavedRequests;

/// <summary>Dashboard v2 spec 5.1: the email a request came from, as read at its v2 save. Every field may be null.</summary>
public sealed record EmailView(string? Subject, DateTime? ReceivedUtc, string? From, string? MessageId);

/// <summary>
/// Dashboard-api-m12 7.4: the PDF a request was saved from, copied from the extraction record at save (never from the
/// client). Null on a request that was not saved from an upload.
/// </summary>
public sealed record UploadView(
    string FileName, long SizeBytes, string Sha256, string Model, string PromptVersion, DateTime ExtractedUtc);

/// <summary>
/// Dashboard v2 spec 5: the answer to a v2 save. The v1 fields, with <c>needsAttention</c> and <c>validation</c> taken
/// from the v2 judgement (spec 5.3, 6.4), plus <c>pushId</c> and <c>email</c>.
/// </summary>
public sealed record SavedV2View(
    long Id,
    string PublicRef,
    SavedRequestStatus Status,
    bool NeedsAttention,
    DuplicateView Duplicate,
    ValidationView? Validation,
    DateTime CreatedUtc,
    string? PushId,
    EmailView? Email);

/// <summary>
/// Dashboard v2 spec 9.1: the v2 snapshot in short. <c>state</c> is the snapshot's state, or <c>none</c> when the
/// request has no v2 snapshot; <c>problemCount</c> is the snapshot's findings and errors plus the metadata errors.
/// </summary>
public sealed record ValidationSummaryV2View(string State, int ProblemCount, DateTime? ValidatedUtc, int? VersionNumber);

/// <summary>Dashboard v2 spec 9.1: the last post. <c>postedCount</c> is null when a person settled it by resolve.</summary>
public sealed record PostSummaryView(string? PostId, int? PostedCount, DateTime? PostedUtc, ActorView? PostedBy);

/// <summary>Dashboard v2 spec 9.1: another not-yet-posted request with the same reference.</summary>
public sealed record OtherPushView(long Id, string PublicRef, string? PushId, DateTime ReceivedUtc, SavedRequestStatus Status);

public sealed record DetailLedgerCountsView(int Submitted, int Posted);

/// <summary>
/// Dashboard v2 spec 10 item 4.9: the ledger batch behind the last attempt of a <c>partial</c> or <c>unknown</c>
/// request. <c>createdUtc</c> is the batch's start time.
/// </summary>
public sealed record DetailLedgerBatchView(string PostId, JobStatus Status, DateTime CreatedUtc, DetailLedgerCountsView Counts);

/// <summary>Dashboard v2 spec 9.1: a v2 list row. The v1 row's fields, with the v2 judgement, plus the v2 additions.</summary>
public sealed record SavedRequestSummaryV2View(
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
    IReadOnlyList<string> AllowedActions,
    string? PushId,
    EmailView? Email,
    int HistoryCount,
    ValidationSummaryV2View ValidationSummary,
    PostSummaryView? PostSummary,
    int SkipCount,
    UploadView? Upload);

/// <summary>
/// Dashboard v2 spec 9.1, 6.4, 10 items 4.1, 4.3, 4.9: one request. The v1 detail's fields with <c>needsAttention</c> and
/// <c>validation</c> from the v2 judgement and <c>readErrors</c> merged (metadata errors, then v1 read errors, then the
/// v2 <c>post</c> errors), plus the list row's additions, <c>otherUnpostedPushes</c> and <c>ledgerBatch</c>.
/// </summary>
public sealed record SavedRequestDetailV2View(
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
    IReadOnlyList<string> AllowedActions,
    string? PushId,
    EmailView? Email,
    int HistoryCount,
    ValidationSummaryV2View ValidationSummary,
    PostSummaryView? PostSummary,
    int SkipCount,
    IReadOnlyList<OtherPushView> OtherUnpostedPushes,
    DetailLedgerBatchView? LedgerBatch,
    UploadView? Upload);

/// <summary>Dashboard v2 spec 9.3: a flag among pending requests and among all.</summary>
public sealed record FlagCountsView(int Pending, int All);

/// <summary>Dashboard v2 spec 9.3. <c>byStatus</c> names every status, zero included.</summary>
public sealed record StatsV2View(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    FlagCountsView NeedsAttention,
    FlagCountsView Duplicate,
    FlagCountsView HasErrors);

/// <summary>Dashboard v2 spec 10 item 4.8: a ledger row with the <c>externalId</c> the caller sent for it.</summary>
public sealed record LedgerPostedRowV2View(
    int Index, string? ExternalId, string TxnId, string RequestId, string Kind, string Account, string? Payee, decimal Amount);

/// <summary>The v1 <see cref="LedgerBatchView"/> with <see cref="LedgerPostedRowV2View"/> rows.</summary>
public sealed record LedgerBatchV2View(
    string BatchId,
    JobStatus Status,
    DirectPostCounts Counts,
    DirectPostTotals Totals,
    string? Error,
    IReadOnlyList<LedgerPostedRowV2View> Posted);

/// <summary>The v1 <see cref="AttemptDetailView"/> with a <see cref="LedgerBatchV2View"/> (spec 10 item 4.8).</summary>
public sealed record AttemptDetailV2View(
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LedgerBatchV2View? LedgerBatch);

/// <summary>The v2 views of a saved request. v1 views (<see cref="SavedRequestViews"/>) never show these fields.</summary>
public static class SavedRequestViewsV2
{
    /// <summary>The value of <c>validationSummary.state</c> when there is no v2 snapshot.</summary>
    public const string NoValidation = "none";

    public static SavedV2View Saved(SavedRequest r) =>
        new(r.Id, r.PublicRef, r.Status, r.NeedsAttentionV2,
            SavedRequestViews.Duplicate(r.DuplicateOfId, r.DuplicateReasons),
            SavedRequestViews.Validation(r.ValidationV2), r.CreatedUtc, PushId(r.Metadata), Email(r.Metadata));

    /// <summary>Spec 5.2: lower case with dashes, or null.</summary>
    public static string? PushId(SaveMetadata m) => m.PushId?.ToString("D");

    /// <summary>Spec 5.1: null when all four columns are null.</summary>
    public static EmailView? Email(SaveMetadata m) =>
        m.HasEmail ? new EmailView(m.EmailSubject, m.EmailReceivedUtc, m.EmailFrom, m.EmailMessageId) : null;

    /// <summary>
    /// Spec 6.4: the v2 <c>readErrors</c>. The metadata errors first, then the v1 read errors of the current version,
    /// then the v2 <c>post</c> errors of its payload.
    /// </summary>
    public static IReadOnlyList<string> ReadErrors(SaveMetadata metadata, IReadOnlyList<string> v1ReadErrors, JsonElement payload) =>
        [.. metadata.MetadataErrors, .. v1ReadErrors, .. NotPostedRows.Read(payload).Errors];

    /// <param name="readErrors">From <see cref="ReadErrors"/>.</param>
    /// <param name="ledger">The batch of the last attempt, only for a partial or unknown request (spec 10 item 4.9).</param>
    public static SavedRequestDetailV2View Detail(
        SavedRequest r, IReadOnlyList<string> readErrors, IReadOnlyList<OtherPush> others, DirectPostResult? ledger,
        Caller c, ActorNames names)
    {
        var v1 = SavedRequestViews.Detail(r, readErrors, c, names);
        return new SavedRequestDetailV2View(
            v1.Id, v1.PublicRef, v1.Status, v1.RowVersion, v1.Source, v1.Reference, v1.ControlTotal, v1.TransactionCount,
            v1.CurrentVersion, r.NeedsAttentionV2, readErrors, v1.Duplicate, SavedRequestViews.Validation(r.ValidationV2),
            v1.Payload, v1.LastAttempt, v1.PostId, v1.DiscardReason, v1.Created, v1.Updated, v1.AllowedActions,
            PushId(r.Metadata), Email(r.Metadata), r.HistoryCount,
            ValidationSummary(r.ValidationV2?.State, r.ProblemCount, r.Metadata, r.ValidationV2?.ValidatedUtc, r.ValidationV2?.VersionNumber),
            PostSummary(r.PostSummary, names), r.SkipCount,
            others.Select(o => new OtherPushView(o.Id, o.PublicRef, o.PushId?.ToString("D"), o.ReceivedUtc, o.Status)).ToList(),
            ledger is null ? null : DetailLedgerBatch(ledger),
            Upload(r.Upload));
    }

    public static SavedRequestSummaryV2View Summary(SavedRequestSummary s, Caller c, ActorNames names)
    {
        var v1 = SavedRequestViews.Summary(s, c, names);
        return new SavedRequestSummaryV2View(
            v1.Id, v1.PublicRef, v1.Status, v1.RowVersion, v1.Source, v1.Reference, v1.ControlTotal, v1.TransactionCount,
            v1.CurrentVersion, s.NeedsAttentionV2, v1.Duplicate,
            s.ValidationV2State is { } state ? new ValidationSummaryView(state, s.ValidationV2Utc) : null,
            v1.PostId, v1.Created, v1.Updated, v1.AllowedActions,
            PushId(s.Metadata), Email(s.Metadata), s.HistoryCount,
            ValidationSummary(s.ValidationV2State, s.ProblemCount, s.Metadata, s.ValidationV2Utc, s.ValidationV2Version),
            PostSummary(s.PostSummary, names), s.SkipCount, Upload(s.Upload));
    }

    /// <summary>Dashboard-api-m12 7.4: the upload columns, or null.</summary>
    public static UploadView? Upload(UploadMeta? u) =>
        u is null ? null : new UploadView(u.FileName, u.SizeBytes, u.Sha256, u.Model, u.PromptVersion, u.ExtractedUtc);

    public static StatsV2View Stats(SavedRequestStatsV2 s) =>
        new(
            s.Total,
            Enum.GetValues<SavedRequestStatus>().ToDictionary(SavedRequestJson.Name, status => s.ByStatus.GetValueOrDefault(status)),
            Flag(s.NeedsAttention),
            Flag(s.Duplicate),
            Flag(s.HasErrors));

    /// <summary>
    /// Spec 10 items 4.8 and 9.3: the attempt, with each ledger row's <c>externalId</c>. The ledger row's own value is
    /// used; when it has none, the attempt's stored <c>result.posted[]</c> row with the same <c>index</c> supplies it.
    /// </summary>
    public static AttemptDetailV2View AttemptDetail(PostAttempt a, ActorNames names, DirectPostResult? ledger)
    {
        var v1 = SavedRequestViews.AttemptDetail(a, names);
        return new AttemptDetailV2View(
            v1.Id, v1.VersionNumber, v1.Outcome, v1.HttpStatus, v1.PostId, v1.Counts, v1.Error, v1.StartedUtc, v1.FinishedUtc,
            v1.StartedBy, v1.ResolvedBy, v1.ResolvedUtc, v1.ResolutionNote, v1.Result,
            ledger is null ? null : LedgerBatch(ledger, ExternalIdsByIndex(a.Result)));
    }

    /// <summary>The actors a v2 record names: the v1 ones and whoever made the last post.</summary>
    public static IEnumerable<string?> ActorsOf(SavedRequest r) => SavedRequestViews.ActorsOf(r).Append(r.PostSummary?.PostedBy);

    public static IEnumerable<string?> ActorsOf(SavedRequestSummary s) => SavedRequestViews.ActorsOf(s).Append(s.PostSummary?.PostedBy);

    private static FlagCountsView Flag(FlagCounts f) => new(f.Pending, f.All);

    private static ValidationSummaryV2View ValidationSummary(
        ValidationState? state, int problemCount, SaveMetadata metadata, DateTime? validatedUtc, int? versionNumber) =>
        new(
            state is { } s ? SavedRequestJson.Name(s) : NoValidation,
            problemCount + metadata.MetadataErrors.Count,
            validatedUtc,
            versionNumber);

    private static PostSummaryView? PostSummary(PostSummary? p, ActorNames names) =>
        p is null ? null : new PostSummaryView(p.PostId, p.PostedCount, p.PostedUtc, p.PostedBy is { } by ? names.Of(by) : null);

    private static DetailLedgerBatchView DetailLedgerBatch(DirectPostResult batch) =>
        new(batch.BatchId, batch.Status, batch.StartedUtc, new DetailLedgerCountsView(batch.Counts.Submitted, batch.Counts.Posted));

    private static LedgerBatchV2View LedgerBatch(DirectPostResult batch, Dictionary<int, string> externalIds) =>
        new(
            batch.BatchId, batch.Status, batch.Counts, batch.Totals, batch.Error,
            batch.Posted.Select(r => new LedgerPostedRowV2View(
                    r.Index, r.ExternalId ?? externalIds.GetValueOrDefault(r.Index), r.TxnId, r.RequestId,
                    JsonNamingPolicy.CamelCase.ConvertName(r.Kind.ToString()), r.Account, r.Payee, r.Amount))
                .ToList());

    /// <summary>The <c>externalId</c> of each stored <c>result.posted[]</c> row, by <c>index</c>.</summary>
    private static Dictionary<int, string> ExternalIdsByIndex(JsonNode? result)
    {
        var ids = new Dictionary<int, string>();
        if (result is not JsonObject o || o["posted"] is not JsonArray posted)
        {
            return ids;
        }

        foreach (var row in posted)
        {
            if (row is JsonObject r
                && r["index"] is JsonValue index && index.TryGetValue<int>(out var i)
                && r["externalId"] is JsonValue id && id.TryGetValue<string>(out var text))
            {
                ids.TryAdd(i, text);
            }
        }

        return ids;
    }
}
