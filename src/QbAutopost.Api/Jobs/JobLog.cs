using System.Globalization;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// What a job did, step by step, for the log (spec §14). At Information a line is named by file, line number, date and
/// amount, never by its statement text; the description and full mapping of every line are at Debug.
/// </summary>
public static class JobLog
{
    public static void Analysis(ILogger log, string jobId, AnalysisResult a)
    {
        var spec = a.Spec.Spec;
        log.LogInformation(
            "Job {JobId}: requirement read ({SpecSource}): company {Company}, kinds {Kinds}, bank {BankLast4}, card {CardLast4}",
            jobId, a.Spec.Source, spec.Company ?? a.Company, Join(spec.Kinds), Join(spec.BankLast4), Join(spec.CardLast4));
        if (a.Spec.Note is { } note)
        {
            log.LogWarning("Job {JobId}: requirement note: {Note}", jobId, note);
        }

        foreach (var file in a.Input.Unreadable)
        {
            log.LogWarning("Job {JobId}: file {File} not read: {Reason}", jobId, file.File, file.Reason);
        }

        foreach (var s in a.Statements)
        {
            if (s.IsHeld)
            {
                log.LogWarning(
                    "Job {JobId}: statement {File} held ({HoldReason}): {Errors}", jobId, s.File, s.HoldReason, string.Join("; ", s.Errors));
            }
            else
            {
                log.LogInformation(
                    "Job {JobId}: statement {File}: {Kind} ending {Last4}, layout {Layout}, {Rows} rows, reconcile {Reconcile}",
                    jobId, s.File, s.Kind, s.Last4, s.Layout ?? "(model-read)", s.Rows, s.Reconcile?.Message);
            }
        }

        if (!a.Gate.Ok)
        {
            log.LogWarning("Job {JobId}: requirement check (G2) failed: {Errors}", jobId, string.Join("; ", a.Gate.Errors));
            return;
        }

        foreach (var i in a.Invoices)
        {
            if (i.Matched)
            {
                log.LogInformation("Job {JobId}: invoice {File} matched line {RequestId}", jobId, i.File, i.Facts!.MatchedRequestId);
            }
            else
            {
                log.LogInformation("Job {JobId}: invoice {File} not matched ({Reason}) {Note}", jobId, i.File, i.Reason, i.Note);
            }
        }

        var held = a.Lines.Where(l => l.Decision == Decision.Hold).ToList();
        var skipped = a.Lines.Where(l => l.Decision == Decision.Skip).ToList();
        log.LogInformation(
            "Job {JobId}: {Lines} lines: {ToPost} to post, {Held} held ({HeldReasons}), {Skipped} skipped ({SkippedReasons})",
            jobId, a.Lines.Count, a.Lines.Count - held.Count - skipped.Count, held.Count, CountByReason(held), skipped.Count, CountByReason(skipped));

        foreach (var line in a.Lines)
        {
            if (line.Decision != Decision.Post)
            {
                log.LogInformation(
                    "Job {JobId}: {Decision} {Line} {Kind}: {Reason} {Note}",
                    jobId, line.Decision == Decision.Hold ? "held" : "skipped", Where(line), line.Kind, line.Reason, line.Note);
            }

            log.LogDebug(
                "Job {JobId}: line {Line} {Kind} {Decision}: account {Account}, payee {Payee}, line account {LineAccount}, tier {Tier}, confidence {Confidence} {ModelConfidence}, description {Description}",
                jobId, Where(line), line.Kind, line.Decision, line.Account, line.Payee, line.LineAccount, line.Tier, line.Confidence, line.ModelConfidence, line.Line.Description);
        }
    }

    /// <summary>Lines whose decision changed between the analysis and posting: the QuickBooks duplicate check (G4).</summary>
    public static void Changes(ILogger log, string jobId, AnalysisResult before, AnalysisResult after)
    {
        if (before.Lines.Count != after.Lines.Count)
        {
            return;
        }

        foreach (var (old, now) in before.Lines.Zip(after.Lines))
        {
            if (old.Decision != now.Decision || old.Reason != now.Reason)
            {
                log.LogInformation(
                    "Job {JobId}: duplicate check (G4) changed {Line} to {Decision}: {Reason} {Note}",
                    jobId, Where(now), now.Decision, now.Reason, now.Note);
            }
        }
    }

    public static void Posting(ILogger log, JobRecord job, PostOutcome outcome)
    {
        foreach (var p in outcome.Posted)
        {
            log.LogInformation(
                "Job {JobId}: posted {Kind} {Line} to {Account}, payee {Payee}, line account {LineAccount} as TxnID {TxnId}",
                job.JobId, p.Txn.Kind, Where(p.Txn), p.Txn.Account, p.Txn.Payee, p.Txn.LineAccount, p.TxnId);
        }

        foreach (var r in outcome.Rejected)
        {
            log.LogWarning("Job {JobId}: not posted {Kind} {Line}: {Reason} {Note}", job.JobId, r.Kind, Where(r), r.Reason, r.Note);
        }

        log.LogInformation(
            "Job {JobId}: batch {BatchId}: {Posted} posted, {Rejected} not posted", job.JobId, job.BatchId, outcome.Posted.Count, outcome.Rejected.Count);
    }

    /// <summary><c>file:line date amount</c> — enough to find the line, without its statement text.</summary>
    private static string Where(MappedTxn t) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{t.Line.SourceFile}:{t.Line.LineNo} {t.Line.Date:yyyy-MM-dd} {Money.Format(t.Line.Amount)} {t.Line.Direction.ToString().ToLowerInvariant()}");

    private static string CountByReason(IEnumerable<MappedTxn> lines)
    {
        var text = string.Join(", ", lines.GroupBy(l => l.Reason ?? "?", StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}"));
        return text.Length == 0 ? "none" : text;
    }

    private static string Join<T>(IEnumerable<T> values)
    {
        var text = string.Join(",", values);
        return text.Length == 0 ? "none" : text;
    }
}
