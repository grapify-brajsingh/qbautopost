using System.Xml;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Api;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Jobs;

/// <summary>
/// FR-A-8: the direct post. The folder path's <see cref="JobPipeline.PostAsync"/> without a folder — same gates, same
/// qbXML, same ledger, so undo (FR-13) and the next duplicate check see a direct batch exactly as they see a job.
/// <para>
/// The order below is the safety property, not an implementation detail: the request and the batch's state are on
/// disk <b>before</b> the first qbXML leaves the process (CLAUDE.md rule 7), so a crash mid-post leaves evidence of
/// what was asked for rather than a silent gap.
/// </para>
/// </summary>
public sealed class DirectPostRunner(
    PipelineOptions options,
    DirectPlanner planner,
    ApiBatchStore batches,
    IQbGateway gateway,
    IClock clock,
    ILogger<DirectPostRunner> log)
{
    /// <summary>FR-A-6/FR-A-8 share this: the post is the plan plus the SDK call.</summary>
    public DirectPlan Plan(DirectRequest request) => planner.Plan(
        request,
        Rules.Load(options.RulesFile),
        new QbListsStore(options.QbListsFile).Load(),
        new LedgerStore(options.LedgerFile).Load(),
        DateOnly.FromDateTime(clock.UtcNow),
        options.QbXmlVersion);

    /// <summary>
    /// §6.1: the batch's job id is <c>api-&lt;reference&gt;</c>, or a dated one when the caller named no reference.
    /// SPEC-GAP T-908: §6.1 builds that fallback from the request id, which does not exist until §2.6 is implemented
    /// (T-911/T-912); a random suffix serves the same purpose — a batch id no earlier batch can collide with.
    /// </summary>
    public string JobIdOf(DirectRequest request) => string.IsNullOrWhiteSpace(request.Reference)
        ? $"api-{clock.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..21]
        : $"api-{request.Reference.Trim()}";

    public string NextBatchId(DirectRequest request) => batches.NextBatchId(JobIdOf(request));

    /// <summary>
    /// Records the batch before any work starts and returns its opening state. A dry run ends here, in <c>ready</c>:
    /// FR-A-6's answer plus a persisted batch, and not one byte sent to QuickBooks.
    /// </summary>
    public DirectPostResult Begin(string batchId, DirectRequest request, DirectPlan plan, bool dryRun)
    {
        batches.SaveRequest(batchId, request);
        var opening = Describe(batchId, request, plan, dryRun, dryRun ? JobStatus.Ready : JobStatus.Posting);
        if (!dryRun)
        {
            batches.SaveStatus(opening);
            return opening;
        }

        log.LogInformation(
            "Batch {BatchId}: dry run, {WouldPost} row(s) ready, {Held} held; nothing sent to QuickBooks",
            batchId, plan.WouldPost, plan.Held);
        var ready = opening with { FinishedUtc = clock.UtcNow };
        batches.SaveResult(ready);
        return ready;
    }

    /// <summary>FR-11/FR-12 for a batch already begun. Runs on the worker, never in parallel with a job.</summary>
    public async Task<DirectPostResult> RunAsync(string batchId, DirectRequest request, DirectPlan plan, CancellationToken ct)
    {
        var opening = Describe(batchId, request, plan, dryRun: false, JobStatus.Posting);
        var folder = batches.FolderOf(batchId);
        var toPost = plan.ToPost;
        var held = plan.Rows.Where(r => r.Outcome == DirectOutcome.Held).ToList();
        var skipped = plan.Rows.Where(r => r.Outcome is DirectOutcome.Skipped or DirectOutcome.Duplicate).ToList();

        if (toPost.Count > 0)
        {
            // FR-11: no posting without a recent backup. Nothing has been sent, so this is a clean refusal.
            if (BackupGuard.Check(options.BackupFolder, options.BackupMaxAgeHours, clock.UtcNow) is { } backupProblem)
            {
                log.LogWarning("Batch {BatchId}: refused before sending anything: {Problem}", batchId, backupProblem);
                return Finish(opening with
                {
                    Status = JobStatus.Failed,
                    Error = backupProblem,
                    Unavailable = true,
                    Held = [.. held, .. toPost.Select(t => Row(plan, t, HoldReasons.BackupTooOld, backupProblem))],
                    Skipped = skipped,
                });
            }

            try
            {
                // G4's QuickBooks half (FR-8). Its queries are audited into the batch folder, like a job's output.
                var decided = await new LiveDuplicateGate(gateway, options.QbXmlVersion, options.DuplicateWindowDays)
                    .CheckAsync(toPost, folder, ct);
                toPost = decided.Where(t => t.Decision == Decision.Post).ToList();
                held = [.. held, .. decided.Where(t => t.Decision == Decision.Hold).Select(t => Row(plan, t))];
                skipped = [.. skipped, .. decided.Where(t => t.Decision == Decision.Skip).Select(t => Row(plan, t))];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only queries were sent, so QuickBooks is unchanged: no ledger record, the batch can be re-sent.
                log.LogWarning("Batch {BatchId}: duplicate check (G4) failed, nothing posted: {Error}", batchId, ex.Message);
                return Finish(opening with
                {
                    Status = JobStatus.Partial,
                    Error = $"nothing posted: duplicate check (G4) failed: {ex.Message}",
                    Unavailable = true,
                    Held = [.. held, .. toPost.Select(t => Row(plan, t, HoldReasons.DuplicateCheckFailed, ex.Message))],
                    Skipped = skipped,
                });
            }
        }

        if (toPost.Count == 0)
        {
            log.LogInformation("Batch {BatchId}: nothing to post ({Held} held, {Skipped} skipped)", batchId, held.Count, skipped.Count);
            return Finish(opening with
            {
                Status = held.Count == 0 ? JobStatus.Posted : JobStatus.Partial,
                Held = held,
                Skipped = skipped,
            });
        }

        var qbXml = QbXmlBuilder.BuildAddRequest(toPost, options.QbXmlVersion);
        batches.SaveRequestQbXml(batchId, qbXml);

        PostVerification verification;
        string? error = null;
        try
        {
            var response = await gateway.ProcessAsync(qbXml, ct);
            batches.SaveResponseQbXml(batchId, response);
            verification = PostVerifier.Verify(toPost, QbXmlParser.ParseAddResponse(response));
        }
        catch (QuickBooksUnavailableException ex)
        {
            // Nothing reached QuickBooks: no ledger record, so the batch can simply be sent again.
            log.LogWarning("Batch {BatchId}: QuickBooks unavailable, nothing posted: {Error}", batchId, ex.Message);
            return Finish(opening with
            {
                Status = JobStatus.Partial,
                Error = $"nothing posted: QuickBooks unavailable ({ex.Message})",
                Unavailable = true,
                Held = [.. held, .. toPost.Select(t => Row(plan, t, HoldReasons.QuickBooksUnavailable, ex.Message))],
                Skipped = skipped,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The request may or may not have been applied. Hold every line and record the batch, so a re-send is a
            // deliberate act by a person who has looked at QuickBooks first.
            var busy = ex is QuickBooksBusyException;
            error = busy
                ? $"{HoldReasons.QuickBooksBusy}: {ex.Message}; check QuickBooks before re-sending"
                : $"quickbooks call failed ({ex.GetType().Name}: {ex.Message}); check QuickBooks before re-sending";
            var reason = busy ? HoldReasons.QuickBooksBusy : HoldReasons.QuickBooksNoResponse;
            var note = busy ? "QuickBooks did not answer in time"
                : ex is FormatException or XmlException ? "unreadable QuickBooks response"
                : "QuickBooks call failed";
            verification = new PostVerification(
                [],
                toPost.Select(t => t with { Decision = Decision.Hold, Reason = reason, Note = note }).ToList());
        }

        RecordInLedger(batchId, JobIdOf(request), verification, held.Count, skipped.Count);

        var posted = verification.Posted;
        held = [.. held, .. verification.Rejected.Select(t => Row(plan, t))];
        log.LogInformation(
            "Batch {BatchId}: {Posted} posted, {Held} held, {Skipped} skipped", batchId, posted.Count, held.Count, skipped.Count);

        return Finish(opening with
        {
            Status = held.Count == 0 && error is null ? JobStatus.Posted : JobStatus.Partial,
            Error = error,
            Posted = [.. posted.Select(p => Posted(plan, p))],
            Held = held,
            Skipped = skipped,
        });
    }

    /// <summary>The batch as it stands, written to <c>result.json</c> — the final word for FR-A-11.</summary>
    private DirectPostResult Finish(DirectPostResult result)
    {
        var final = result with
        {
            FinishedUtc = clock.UtcNow,
            Counts = new DirectPostCounts(result.Counts.Submitted, result.Posted.Count, result.Held.Count, result.Skipped.Count),
            Totals = result.Totals with { Posted = result.Posted.Sum(p => p.Amount) },
        };
        batches.SaveResult(final);
        return final;
    }

    private DirectPostResult Describe(
        string batchId, DirectRequest request, DirectPlan plan, bool dryRun, JobStatus status) => new()
        {
            BatchId = batchId,
            Reference = request.Reference,
            Status = status,
            DryRun = dryRun,
            CompanyFile = options.CompanyName,
            StartedUtc = clock.UtcNow,
            Counts = new DirectPostCounts(plan.Submitted, 0, plan.Held, plan.Skipped),
            Totals = new DirectPostTotals(plan.SubmittedTotal, 0m),
            Held = [.. plan.Rows.Where(r => r.Outcome == DirectOutcome.Held)],
            Skipped = [.. plan.Rows.Where(r => r.Outcome is DirectOutcome.Skipped or DirectOutcome.Duplicate)],
        };

    /// <summary>A mapped line back in the caller's terms, so every row of the answer carries their own id.</summary>
    private static DirectPlanRow Row(DirectPlan plan, MappedTxn txn, string? reason = null, string? note = null)
    {
        var planned = plan.Rows.FirstOrDefault(r => r.RequestId == txn.RequestId);
        return new DirectPlanRow
        {
            Index = planned?.Index ?? txn.Line.LineNo - 1,
            ExternalId = planned?.ExternalId,
            RequestId = txn.RequestId,
            Outcome = txn.Decision == Decision.Skip ? DirectOutcome.Skipped : DirectOutcome.Held,
            Kind = txn.Kind,
            Account = txn.Account,
            LineAccount = txn.LineAccount,
            Payee = txn.Payee,
            Amount = txn.Line.Amount,
            Confidence = txn.Confidence,
            Reason = reason ?? txn.Reason,
            Note = note ?? txn.Note,
            Candidates = txn.Candidates,
        };
    }

    private static DirectPostedRow Posted(DirectPlan plan, PostedLine line)
    {
        var planned = plan.Rows.FirstOrDefault(r => r.RequestId == line.Txn.RequestId);
        return new DirectPostedRow(
            planned?.Index ?? line.Txn.Line.LineNo - 1,
            planned?.ExternalId,
            line.Txn.RequestId,
            line.TxnId,
            line.EditSequence,
            line.Txn.Kind,
            line.Txn.Account!,
            line.Txn.LineAccount,
            line.Txn.Payee,
            line.Txn.Line.Amount);
    }

    /// <summary>
    /// Spec §13. The same ledger a folder job writes: one <see cref="LedgerJob"/> plus an entry per posted line, so
    /// G4 catches a later repeat from either entrance and undo can find this batch by id.
    /// </summary>
    private void RecordInLedger(string batchId, string jobId, PostVerification verification, int held, int skipped)
    {
        var store = new LedgerStore(options.LedgerFile);
        var ledger = store.Load();
        var entries = verification.Posted.Select(p => new LedgerEntry
        {
            BatchId = batchId,
            JobId = jobId,
            Fingerprint = p.Txn.Line.Fingerprint,
            TxnId = p.TxnId,
            EditSequence = p.EditSequence,
            Kind = p.Txn.Kind,
            Account = p.Txn.Account!,
            Payee = p.Txn.Payee,
            LineAccount = p.Txn.LineAccount,
            Amount = p.Txn.Line.Amount,
            Date = p.Txn.Line.Date,
            RefNumber = p.Txn.RefNumber,
            Memo = p.Txn.Memo,
            SourceFile = p.Txn.Line.SourceFile,
            LineNo = p.Txn.Line.LineNo,
        }).ToList();

        var batch = new LedgerJob
        {
            JobId = jobId,
            BatchId = batchId,
            PostedUtc = clock.UtcNow,
            Posted = entries.Count,
            Held = held + verification.Rejected.Count,
            Skipped = skipped,
            TxnIds = entries.Select(e => e.TxnId).ToList(),
        };

        store.Save(ledger with { Jobs = [.. ledger.Jobs, batch], Posted = [.. ledger.Posted, .. entries] });
    }
}
