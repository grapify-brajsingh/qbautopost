using QbAutopost.Api.Jobs;
using QbAutopost.Core.Api;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// Saved-requests spec 6.4: the offline plan validate-and-post would make for one saved version, as a snapshot. It is
/// <see cref="DirectPostRunner.Plan"/> plus the request-level refusals of <see cref="SyncPostRunner"/>, so the
/// dashboard and the post cannot disagree about what a payload means. Nothing here opens a QuickBooks session.
/// <para>
/// The order is the runner's: unreadable, then the two unsupported fields, then the plan's own refusals (control total,
/// row count, malformed rows), then held rows. A snapshot is advisory; Post always validates again.
/// </para>
/// </summary>
public sealed class SavedRequestValidator(DirectPostRunner runner, ILogger<SavedRequestValidator> log)
{
    /// <summary>Shown to callers when the plan threw for a reason other than the rules file; never an exception message.</summary>
    public const string CouldNotRun = "validation could not run";

    /// <summary>Shown to callers when the rules file is missing (spec 10.8).</summary>
    public const string RulesMissing = "rules file missing";

    /// <summary>
    /// Dashboard v2 spec 6.4: the v1 and the v2 judgement of one version, from the same inputs. v1 plans with no row
    /// set aside, exactly as <see cref="Validate"/> always has. v2 plans with the rows marked <c>post: false</c> set
    /// aside; a <c>post</c> that is not a boolean makes the v2 snapshot unreadable, naming its path, and leaves the v1
    /// snapshot as it is.
    /// </summary>
    /// <param name="savedRequestId">For the log line only; null while a new request is being saved.</param>
    public (ValidationSnapshot V1, ValidationSnapshot V2) ValidateBoth(
        PayloadRead read, NotPostedRead notPosted, int versionNumber, DateTime nowUtc, long? savedRequestId = null)
    {
        var v1 = Validate(read, versionNumber, nowUtc, savedRequestId);
        if (notPosted.Errors.Count > 0)
        {
            // v1's read errors come first, as the v2 detail lists them (spec 6.4, Display).
            return (v1, Snapshot(ValidationState.Unreadable, versionNumber, nowUtc, [.. read.Errors, .. notPosted.Errors]));
        }

        return (v1, Validate(read, versionNumber, nowUtc, savedRequestId, notPosted.Indexes));
    }

    /// <param name="savedRequestId">For the log line only; null while a new request is being saved.</param>
    /// <param name="notPosted">
    /// Dashboard v2: the rows to set aside as <c>not-to-post</c>. Null (the default) plans every row, as v1 does.
    /// </param>
    public ValidationSnapshot Validate(
        PayloadRead read, int versionNumber, DateTime nowUtc, long? savedRequestId = null, IReadOnlySet<int>? notPosted = null)
    {
        if (!read.Readable)
        {
            return Snapshot(ValidationState.Unreadable, versionNumber, nowUtc, read.Errors);
        }

        var request = read.Request!;
        if (SyncPostRunner.UnsupportedFields(request) is { Count: > 0 } unsupported)
        {
            return Snapshot(ValidationState.Refused, versionNumber, nowUtc, unsupported);
        }

        DirectPlan plan;
        try
        {
            plan = runner.Plan(notPosted is null ? request.ToDirectRequest() : request.ToDirectRequest(notPosted));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // R24, spec 6.1 step 4 and 10.8: whatever stops the plan (rules file missing, a list that cannot be read,
            // a bug), the check is down, not the request. The save or edit still succeeds with an `unavailable`
            // snapshot, so the payload is never lost. The text callers see is fixed: an exception message can name a
            // path on this server. The log carries the exception type and the id, never the message.
            log.LogWarning(
                "Validation of saved request {SavedRequestId} version {Version} could not run: {ExceptionType}",
                savedRequestId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "new", versionNumber,
                ex.GetType().Name);
            return Snapshot(
                ValidationState.Unavailable, versionNumber, nowUtc,
                [ex is QbAutopost.Core.Mapping.RulesFileNotFoundException ? RulesMissing : CouldNotRun]);
        }

        if (plan.Errors.Count > 0)
        {
            return Snapshot(ValidationState.Refused, versionNumber, nowUtc, plan.Errors);
        }

        var findings = SyncPostFindings.From(plan);
        return new ValidationSnapshot(
            plan.Ok ? ValidationState.Ok : ValidationState.Held,
            nowUtc,
            versionNumber,
            findings.Counts,
            findings.Validation,
            [],
            findings.UnknownNames);
    }

    private static ValidationSnapshot Snapshot(
        ValidationState state, int versionNumber, DateTime nowUtc, IReadOnlyList<string> errors) =>
        new(state, nowUtc, versionNumber, null, [], errors.ToList(), null);
}
