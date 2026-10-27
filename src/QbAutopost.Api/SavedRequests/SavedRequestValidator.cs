using System.Text.Json;
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
    public ValidationSnapshot Validate(PayloadRead read, int versionNumber, DateTime nowUtc)
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
            plan = runner.Plan(request.ToDirectRequest());
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Spec 6.1 step 4 and 10.8: the rules file is missing (T-920's RulesFileNotFoundException is an
            // IOException) or a list cannot be read. The check is down, not the request: the save still succeeds and
            // the snapshot says so. The message names a path on this server, never a secret.
            log.LogWarning("Validation of saved version {Version} could not run: {Error}", versionNumber, ex.Message);
            return Snapshot(ValidationState.Unavailable, versionNumber, nowUtc, [ex.Message]);
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
