using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Jobs;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;
using QbAutopost.Core.Store;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// T-1014 (saved-requests spec 7.2, 9, 10.2-10.6): posts the current version of a saved request through
/// validate-and-post's own engine, <see cref="SyncPostRunner"/>, and records how it ended.
/// <para>
/// The first anti-goal is a double post, and every step is ordered against it. The claim (pending to posting, with an
/// in-progress attempt) is committed before the engine runs, so exactly one caller can send. Nothing is retried
/// automatically. The caller's connection bounds nothing after the claim: the engine and the recording run under
/// <see cref="CancellationToken.None"/>, so a browser that closes cannot stop a post half-way or leave it unrecorded
/// (spec 10.5). And no ending that may have reached QuickBooks returns the request to pending: an exception once the
/// worker had started the post (ruling R3) is <c>unknown</c>, and a result that cannot be recorded leaves the request
/// <c>posting</c>, with the result in an evidence file, until start-up recovery marks it <c>unknown</c> (spec 10.6).
/// </para>
/// </summary>
public sealed class SavedRequestPoster(
    ISavedRequestStore store,
    SyncPostRunner runner,
    IClock clock,
    IOptions<AppSettings> settings,
    IOptions<HttpJsonOptions> json,
    ILogger<SavedRequestPoster> log)
{
    /// <summary>The evidence file written beside a post's batch evidence when its result cannot be recorded (spec 10.6).</summary>
    public const string EvidenceFile = "saved-request.json";

    /// <summary>Where the evidence of an attempt without a post id goes, under <c>Paths:ApiBatches</c>.</summary>
    public const string EvidenceFolder = "saved-requests";

    /// <summary>The fields the post-by-id answer adds to validate-and-post's (spec 7.2).</summary>
    public const string SavedRequestIdField = "savedRequestId";
    public const string AttemptIdField = "attemptId";

    /// <summary>
    /// SPEC-GAP T-1014: spec 7.2 calls this the request's new <c>status</c>, but <c>status</c> is already the HTTP
    /// status number of every problem body (RFC 7807) and the post status of every 200 body. Overwriting either would
    /// break the promise that a validate-and-post client reads the answer unchanged, so the field is named for what
    /// it is.
    /// </summary>
    public const string SavedRequestStatusField = "savedRequestStatus";

    /// <summary>Waits after the first and second failed tries to record a result; the third failure falls back to the evidence file.</summary>
    private static readonly TimeSpan[] CompleteBackoff = [TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)];

    public async Task<SavedPostOutcome> PostAsync(
        long id, int? expectedRowVersion, bool confirmDuplicate, Caller caller, CancellationToken ct)
    {
        // 1. Spec 9: a flagged request needs the person to say so. Checked before the claim, so no attempt is created.
        var current = await store.GetAsync(id, ct);
        if (current is null)
        {
            return new SavedPostOutcome(SavedPostKind.NotFound, null, null, null, null);
        }

        if (current.Status == SavedRequestStatus.Pending && IsFlagged(current) && !confirmDuplicate)
        {
            log.LogInformation(
                "Saved request {SavedRequestId}: post refused, it may duplicate {DuplicateOfId} and was not confirmed",
                id, current.DuplicateOfId);
            return new SavedPostOutcome(SavedPostKind.DuplicateUnconfirmed, null, null, current, StatusCodes.Status409Conflict);
        }

        // 2. Spec 10.2: the claim, committed before anything is sent. From here on the caller's token bounds nothing:
        // a claim abandoned half-way would leave a request posting with nothing running for it.
        var claim = await store.TryClaimAsync(
            id, expectedRowVersion, caller.Actor, acknowledgeDuplicate: confirmDuplicate, clock.UtcNow, CancellationToken.None);
        switch (claim.Kind)
        {
            case ClaimKind.NotFound:
                return new SavedPostOutcome(SavedPostKind.NotFound, null, null, null, null);
            case ClaimKind.Stale:
                return new SavedPostOutcome(SavedPostKind.Stale, null, null, claim.Record, StatusCodes.Status412PreconditionFailed);
            case ClaimKind.AlreadyPosting:
                return new SavedPostOutcome(
                    SavedPostKind.AlreadyPosting, null, claim.AttemptId, claim.Record, StatusCodes.Status409Conflict);
            case ClaimKind.AlreadyPosted:
                return new SavedPostOutcome(SavedPostKind.AlreadyPosted, null, null, claim.Record, StatusCodes.Status409Conflict);
            case ClaimKind.NotPostable:
                return new SavedPostOutcome(SavedPostKind.NotPostable, null, null, claim.Record, StatusCodes.Status409Conflict);
        }

        var attemptId = claim.AttemptId!.Value;
        var record = claim.Record!;
        using var scope = log.BeginScope(new Dictionary<string, object> { ["savedRequestId"] = id, ["attemptId"] = attemptId });
        log.LogInformation(
            "Saved request {SavedRequestId}: attempt {AttemptId} claimed version {Version} for {Actor}",
            id, attemptId, claim.VersionNumber, caller.Actor);

        // 3. Spec 7.2: the route binds the stored payload itself, so an unbindable version is a 400 naming each path.
        var read = SavedPayloadReader.Read(record.CurrentPayload);
        if (!read.Readable)
        {
            var refused = new SyncPost(SyncPostOutcome.Refused, read.Errors, null, null, false);
            var status = await FinishAsync(record, attemptId, refused, new PostEnding(PostEndingKind.Unreadable, null, []));
            return new SavedPostOutcome(SavedPostKind.Unreadable, refused, attemptId, record, StatusCodes.Status400BadRequest, status);
        }

        // 4-5. The engine, under its own token (validate-and-post 7). R3: `sending` is set by the engine as the first
        // step on the worker, so an exception tells "nothing was started" from "QuickBooks may have it".
        var sending = false;
        SyncPost post;
        try
        {
            post = await runner.RunAsync(read.Request!, CancellationToken.None, () => sending = true);
        }
        catch (Exception ex)
        {
            return await FailAsync(record, attemptId, ex, sending);
        }

        // 6.
        var newStatus = await FinishAsync(record, attemptId, post, EndingOf(post));
        return new SavedPostOutcome(SavedPostKind.Ran, post, attemptId, record, SyncPostResponses.StatusOf(post), newStatus);
    }

    /// <summary>The fields the saved route adds to validate-and-post's answer (spec 7.2).</summary>
    public static Dictionary<string, object?> ExtraFields(long savedRequestId, long attemptId, SavedRequestStatus status) => new()
    {
        [SavedRequestIdField] = savedRequestId,
        [AttemptIdField] = attemptId,
        [SavedRequestStatusField] = SavedRequestJson.Name(status),
    };

    private static bool IsFlagged(SavedRequest record) => record.DuplicateOfId is not null || record.DuplicateReasons.Count > 0;

    /// <summary>The engine's ending in the terms of spec 7.2's table.</summary>
    private static PostEnding EndingOf(SyncPost post) => post.Outcome switch
    {
        SyncPostOutcome.Refused => new PostEnding(PostEndingKind.Refused, null, []),
        SyncPostOutcome.Held => new PostEnding(PostEndingKind.Held, null, []),
        SyncPostOutcome.Busy => new PostEnding(PostEndingKind.Busy, null, []),
        _ when post.Unavailable => new PostEnding(PostEndingKind.Unavailable, null, []),
        // A 200 without a result is unexplained: Completed with no status, which AttemptOutcomes locks as unknown.
        _ => new PostEnding(
            PostEndingKind.Completed,
            post.Result?.Status,
            post.Result?.Held.Select(h => h.Reason).OfType<string>().ToList() ?? []),
    };

    /// <summary>Records an ending the engine returned, and answers the request's status afterwards.</summary>
    private async Task<SavedRequestStatus> FinishAsync(SavedRequest record, long attemptId, SyncPost post, PostEnding ending)
    {
        var verdict = AttemptOutcomes.From(ending);
        var options = json.Value.SerializerOptions;
        var body = SyncPostResponses.BodyOf(post, ExtraFields(record.Id, attemptId, verdict.NextStatus), options);
        JsonNode? counts = post.Outcome switch
        {
            SyncPostOutcome.Held => JsonSerializer.SerializeToNode(post.Findings!.Counts, options),
            SyncPostOutcome.Posted when post.Result is { } result => JsonSerializer.SerializeToNode(result.Counts, options),
            _ => null,
        };
        var error = post.Outcome == SyncPostOutcome.Refused ? string.Join("; ", post.Errors) : post.Result?.Error;
        return await RecordAsync(
            record.Id, attemptId, verdict,
            new AttemptCompletion(
                verdict.Outcome, verdict.NextStatus, verdict.HttpStatus, post.Result?.PostId, body, counts, error, clock.UtcNow));
    }

    /// <summary>
    /// The engine threw. Before the worker started the post nothing was sent: <c>error</c> (or <c>unavailable</c> for a
    /// missing rules file, spec 10.8) and back to pending. After, QuickBooks may have it: <c>unknown</c>, locked.
    /// </summary>
    private async Task<SavedPostOutcome> FailAsync(SavedRequest record, long attemptId, Exception ex, bool sending)
    {
        var kind = sending ? PostEndingKind.ErrorAfterSend
            : ex is RulesFileNotFoundException ? PostEndingKind.Unavailable
            : PostEndingKind.ErrorBeforeSend;
        var verdict = AttemptOutcomes.From(new PostEnding(kind, null, []));
        log.LogError(
            ex,
            "Saved request {SavedRequestId}: attempt {AttemptId} failed {When}; recorded as {Outcome}",
            record.Id, attemptId, sending ? "after the post was started" : "before anything was sent", verdict.Outcome);

        var problem = ProblemFor(record, kind, ex);
        var options = json.Value.SerializerOptions;
        foreach (var (key, value) in ExtraFields(record.Id, attemptId, verdict.NextStatus))
        {
            problem.Extensions[key] = value;
        }

        var status = await RecordAsync(
            record.Id, attemptId, verdict,
            new AttemptCompletion(
                verdict.Outcome, verdict.NextStatus, verdict.HttpStatus, null,
                JsonSerializer.SerializeToNode(problem, options), null, $"{ex.GetType().Name}: {ex.Message}", clock.UtcNow));
        problem.Extensions[SavedRequestStatusField] = SavedRequestJson.Name(status);
        return new SavedPostOutcome(SavedPostKind.Ran, null, attemptId, record, problem.Status, status, problem);
    }

    /// <summary>
    /// The answer to an engine exception. The exception's own text is logged, never returned: it can name server paths.
    /// The missing rules file is the exception, answered exactly as T-920's handler answers validate-and-post.
    /// </summary>
    private static ProblemDetails ProblemFor(SavedRequest record, PostEndingKind kind, Exception ex) => kind switch
    {
        PostEndingKind.Unavailable => TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: RulesFileExceptionHandler.Title,
            detail: RulesFileExceptionHandler.DetailOf((RulesFileNotFoundException)ex)).ProblemDetails,
        PostEndingKind.ErrorAfterSend => TypedResults.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "The post's ending is not known",
            type: "post-outcome-unknown",
            detail: $"The post of {record.PublicRef} was started and failed before its ending was known, so QuickBooks "
                    + "may or may not have it. It is locked as unknown: check QuickBooks, then an admin resolves it. "
                    + "Do not send it again.").ProblemDetails,
        _ => TypedResults.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "The post could not be started",
            type: "post-not-started",
            detail: $"Nothing was sent to QuickBooks and {record.PublicRef} is pending again. The error is in the "
                    + "server log under this request's id.").ProblemDetails,
    };

    /// <summary>
    /// Spec 10.6: records the ending, trying three times. If every try fails the request stays <c>posting</c>, which no
    /// route can post or edit, and the result goes to an evidence file so the person resolving it later can see what
    /// QuickBooks answered. Returns the request's status as it now stands.
    /// </summary>
    private async Task<SavedRequestStatus> RecordAsync(
        long savedRequestId, long attemptId, AttemptVerdict verdict, AttemptCompletion completion)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await store.CompleteAttemptAsync(attemptId, completion, CancellationToken.None);
                log.LogInformation(
                    "Saved request {SavedRequestId}: attempt {AttemptId} recorded as {Outcome}, request now {Status}, post {PostId}",
                    savedRequestId, attemptId, verdict.Outcome, verdict.NextStatus, completion.PostId);
                return verdict.NextStatus;
            }
            catch (Exception ex) when (attempt < CompleteBackoff.Length)
            {
                log.LogWarning(
                    "Saved request {SavedRequestId}: recording attempt {AttemptId} failed ({Error}); trying again",
                    savedRequestId, attemptId, ex.GetType().Name);
                await Task.Delay(CompleteBackoff[attempt]);
            }
            catch (Exception ex)
            {
                var file = WriteEvidence(savedRequestId, attemptId, completion);
                log.LogError(
                    ex,
                    "Saved request {SavedRequestId}: the result of attempt {AttemptId} (post {PostId}) could not be recorded "
                    + "after 3 tries; evidence {EvidenceFile}. It stays posting until start-up recovery marks it unknown",
                    savedRequestId, attemptId, completion.PostId, file ?? "not written");
                return SavedRequestStatus.Posting;
            }
        }
    }

    /// <summary>
    /// The evidence file: <c>&lt;ApiBatches&gt;/&lt;postId&gt;/saved-request.json</c> beside the batch's own evidence, or
    /// <c>&lt;ApiBatches&gt;/saved-requests/&lt;attemptId&gt;.json</c> when there is no post id. It holds what the attempt
    /// would have recorded and nothing else, so no secret can reach it. Returns the path, or null if it too failed.
    /// </summary>
    private string? WriteEvidence(long savedRequestId, long attemptId, AttemptCompletion completion)
    {
        try
        {
            var root = settings.Value.Paths.ApiBatches;
            var path = completion.PostId is { } postId
                ? Path.Combine(new ApiBatchStore(root).FolderOf(postId), EvidenceFile)
                : Path.Combine(root, EvidenceFolder, $"{attemptId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var evidence = new JsonObject
            {
                [SavedRequestIdField] = savedRequestId,
                [AttemptIdField] = attemptId,
                ["outcome"] = SavedRequestJson.Name(completion.Outcome),
                ["httpStatus"] = completion.HttpStatus,
                ["postId"] = completion.PostId,
                ["nextStatus"] = SavedRequestJson.Name(completion.NextStatus),
                ["finishedUtc"] = completion.FinishedUtc,
                ["error"] = completion.Error,
                ["counts"] = completion.Counts?.DeepClone(),
                ["result"] = completion.Result?.DeepClone(),
            };
            File.WriteAllText(path, evidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch (Exception ex)
        {
            log.LogError(
                ex, "Saved request {SavedRequestId}: the evidence file for attempt {AttemptId} could not be written either",
                savedRequestId, attemptId);
            return null;
        }
    }
}
