using System.Xml;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Output;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Pipeline;

/// <summary>
/// The job pipeline (spec §8): <see cref="RunAnalysisAsync"/> = FR-1…FR-9, <see cref="RunPostAsync"/> = FR-10…FR-12.
/// Status transitions belong to the caller (the job runner); this class only computes and writes output files.
/// Amounts are never computed here — they flow from the parsed statement rows (CLAUDE.md rule 3).
/// </summary>
public sealed class JobPipeline(
    PipelineOptions options,
    ISpecReader specReader,
    StatementReader statementReader,
    InvoiceExtractor invoiceExtractor,
    AccountChooser accountChooser,
    IQbGateway gateway,
    IClock clock)
{
    /// <summary>Folder → spec → statements → G1/G2 → invoices → mapping → job gates → tiers 3–4 + G3 → qbXML and sheets.</summary>
    public async Task<AnalysisResult> RunAnalysisAsync(JobRecord job, CancellationToken ct)
    {
        var input = FolderReader.Read(job.Folder);
        var rules = Rules.Load(options.RulesFile);
        var ledger = new LedgerStore(options.LedgerFile).Load();
        var lists = new QbListsStore(options.QbListsFile).Load();

        var spec = await specReader.ReadAsync(input, ct);
        var statements = new List<StatementSummary>();
        foreach (var file in input.Statements)
        {
            statements.Add(await ReadStatementAsync(file, rules, spec.Spec, input.OutputDir, ct));
        }

        foreach (var statement in statements)
        {
            JobOutputWriter.WriteRows(input.OutputDir, statement);
        }

        var analysis = new AnalysisResult
        {
            Input = input,
            Company = options.CompanyName,
            Spec = spec,
            Gate = SpecGate.Check(spec.Spec, options.CompanyName, statements, rules),
            Statements = statements,
        };
        JobOutputWriter.WriteSpec(input.OutputDir, analysis);
        if (!analysis.Gate.Ok)
        {
            return analysis;
        }

        var lines = statements.Where(s => !s.IsHeld).SelectMany(s => s.Lines).ToList();
        var invoices = await ReadInvoicesAsync(input, lines, rules, ct);
        var matched = invoices.Where(i => i.Matched).Select(i => i.Facts!).ToList();
        var mapped = ApplyJobGates(new Mapper(rules, lists, ledger.Posted, matched).MapAll(lines), spec.Spec, ledger);

        // Tiers 3–4 after the job gates, so duplicates, posted lines and unrequested kinds never reach Hermes.
        mapped = await new ModelTiers(accountChooser).ResolveAsync(
            mapped,
            new ModelTierInput(rules.ModelConfidenceThreshold, lists, ledger.Posted, matched, Path.Combine(input.OutputDir, HermesSpecReader.HermesDir)),
            ct);
        analysis = analysis with
        {
            Invoices = invoices,
            Lines = mapped,
            QbXml = QbXmlBuilder.BuildAddRequest(mapped.Where(m => m.Decision == Decision.Post).ToList(), options.QbXmlVersion),
        };

        JobOutputWriter.WriteRequestAndSheets(input.OutputDir, analysis);
        JobOutputWriter.WriteAnalysis(input.OutputDir, analysis);
        return analysis;
    }

    /// <summary>
    /// The job's next attempt number and batch id (spec §3: batch id = job id + attempt), counting batches already in the
    /// ledger so a forced re-run never reuses a batch id.
    /// </summary>
    public JobRecord BeginPosting(JobRecord job)
    {
        var ledgerAttempts = new LedgerStore(options.LedgerFile).Load().Jobs
            .Count(j => string.Equals(j.JobId, job.JobId, StringComparison.OrdinalIgnoreCase));
        var attempt = Math.Max(job.Attempt, ledgerAttempts) + 1;
        return job.MoveTo(JobStatus.Posting) with { Attempt = attempt, BatchId = Batch.MakeId(job.JobId, attempt) };
    }

    /// <summary>FR-10: re-runs the analysis (rules may have changed), then posts.</summary>
    public async Task<PostOutcome> RunPostAsync(JobRecord job, CancellationToken ct)
    {
        var analysis = await RunAnalysisAsync(job, ct);
        return await PostAsync(job, analysis, ct);
    }

    /// <summary>FR-11/FR-12 for an analysis of <paramref name="job"/>, which must be in <c>posting</c> with a batch id.</summary>
    public async Task<PostOutcome> PostAsync(JobRecord job, AnalysisResult analysis, CancellationToken ct)
    {
        if (job.Status != JobStatus.Posting || job.BatchId is null)
        {
            throw new InvalidOperationException($"Job {job.JobId} is not posting ({job.Status}, batch {job.BatchId}).");
        }

        if (analysis.FailReason is { } failReason)
        {
            // Nothing was sent; the state machine only allows posting → posted|partial.
            return new PostOutcome { Status = JobStatus.Partial, Error = "nothing posted: " + failReason, Analysis = analysis };
        }

        if (analysis.ToPost.Count > 0)
        {
            try
            {
                analysis = await CheckQuickBooksDuplicatesAsync(analysis, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Only queries were sent, so nothing changed in QuickBooks: no ledger record, the job can be re-run.
                return NothingPosted(analysis, $"duplicate check (G4) failed: {ex.Message}", ex.Message);
            }
        }

        var toPost = analysis.ToPost;
        var analysisHeld = analysis.Lines.Count(l => l.Decision == Decision.Hold);
        if (toPost.Count == 0)
        {
            return new PostOutcome { Status = analysisHeld == 0 ? JobStatus.Posted : JobStatus.Partial, Analysis = analysis };
        }

        PostVerification verification;
        string? error = null;
        try
        {
            // TODO(T-604): retry-once and backup-age guard.
            var response = await gateway.ProcessAsync(analysis.QbXml, ct);
            JobOutputWriter.WriteResponse(analysis.Input.OutputDir, response);
            verification = PostVerifier.Verify(toPost, QbXmlParser.ParseAddResponse(response));
        }
        catch (QuickBooksUnavailableException ex)
        {
            // Nothing reached QuickBooks: no ledger record, so the job can simply be re-run.
            return NothingPosted(analysis, $"QuickBooks unavailable ({ex.Message})", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The request may or may not have been applied: hold every line and record the batch so a re-run is refused (F3).
            var busy = ex is QuickBooksBusyException;
            error = busy
                ? $"{HoldReasons.QuickBooksBusy}: {ex.Message}; run duplicates before re-post"
                : $"quickbooks call failed ({ex.GetType().Name}: {ex.Message}); run duplicates before re-post";
            var reason = busy ? HoldReasons.QuickBooksBusy : HoldReasons.QuickBooksNoResponse;
            var note = busy ? "QuickBooks did not answer in time; check QuickBooks before re-posting"
                : ex is FormatException or XmlException ? "unreadable QuickBooks response"
                : "QuickBooks call failed";
            verification = new PostVerification(
                [],
                toPost.Select(t => t with { Decision = Decision.Hold, Reason = reason, Note = note }).ToList());
        }

        RecordInLedger(job, analysis, verification);

        var held = analysisHeld + verification.Rejected.Count;
        return new PostOutcome
        {
            Status = held == 0 && error is null ? JobStatus.Posted : JobStatus.Partial,
            Error = error,
            Analysis = analysis,
            Posted = verification.Posted,
            Rejected = verification.Rejected,
        };
    }

    /// <summary>
    /// FR-8 G4 against QuickBooks (posting only). The analysis output (<c>analysis.json</c>, <c>request.qbxml</c>, sheets)
    /// is rewritten so it shows what is actually sent.
    /// </summary>
    private async Task<AnalysisResult> CheckQuickBooksDuplicatesAsync(AnalysisResult analysis, CancellationToken ct)
    {
        var gate = new LiveDuplicateGate(gateway, options.QbXmlVersion, options.DuplicateWindowDays);
        var lines = await gate.CheckAsync(analysis.Lines, analysis.Input.OutputDir, ct);
        if (lines.SequenceEqual(analysis.Lines))
        {
            return analysis;
        }

        analysis = analysis with
        {
            Lines = lines,
            QbXml = QbXmlBuilder.BuildAddRequest(lines.Where(m => m.Decision == Decision.Post).ToList(), options.QbXmlVersion),
        };
        JobOutputWriter.WriteRequestAndSheets(analysis.Input.OutputDir, analysis);
        JobOutputWriter.WriteAnalysis(analysis.Input.OutputDir, analysis);
        return analysis;
    }

    /// <summary>A post that sent no transaction: <c>partial</c> "nothing posted", every postable line held, no ledger record.</summary>
    private static PostOutcome NothingPosted(AnalysisResult analysis, string why, string note) => new()
    {
        Status = JobStatus.Partial,
        Error = "nothing posted: " + why,
        Analysis = analysis,
        Rejected = analysis.ToPost
            .Select(t => t with { Decision = Decision.Hold, Reason = HoldReasons.QuickBooksUnavailable, Note = note })
            .ToList(),
    };

    private async Task<StatementSummary> ReadStatementAsync(JobFile file, Rules rules, JobSpec spec, string outputDir, CancellationToken ct)
    {
        var parsed = await statementReader.ReadAsync(file, rules, Path.Combine(outputDir, HermesSpecReader.HermesDir), ct);
        var summary = new StatementSummary
        {
            File = parsed.File,
            Last4 = parsed.Last4 ?? file.Last4FromName,
            Kind = parsed.Kind,
            Layout = parsed.Layout,
            Rows = parsed.Rows.Count,
            Lines = parsed.Rows,
            Totals = parsed.Totals,
            HoldReason = parsed.HoldReason,
            Errors = parsed.Errors,
        };
        if (summary.IsHeld)
        {
            return summary;
        }

        // FR-4: T2 output is checked against the statement's own totals; CSV/XLSX against the balance column.
        var reconcile = parsed.Totals is { } totals
            ? ReconcileGate.CheckExtraction(parsed.Kind!.Value, parsed.Rows, totals)
            : ReconcileGate.CheckBalanceChain(parsed.Rows);
        summary = summary with { Reconcile = reconcile };
        if (!reconcile.Ok)
        {
            return summary with { HoldReason = HoldReasons.ReconcileFailed };
        }

        // SPEC-GAP T-103: a statement whose layout kind contradicts the requirement (bank vs card) is held.
        var statedAsBank = spec.BankLast4.Contains(summary.Last4!);
        var statedAsCard = spec.CardLast4.Contains(summary.Last4!);
        var contradicts = (summary.Kind == SourceKind.Bank && statedAsCard && !statedAsBank)
                          || (summary.Kind == SourceKind.Card && statedAsBank && !statedAsCard);
        return contradicts
            ? summary with { HoldReason = HoldReasons.KindMismatch, Errors = [$"layout says {summary.Kind}, requirement says otherwise"] }
            : summary;
    }

    /// <summary>
    /// FR-5: every invoice file → T3 → matched against the lines of statements that passed G1. Each result is written to
    /// <c>output/invoices/&lt;file&gt;.json</c>. An unreadable invoice is reported, never fatal.
    /// </summary>
    private async Task<IReadOnlyList<InvoiceSummary>> ReadInvoicesAsync(
        JobInput input, IReadOnlyList<StatementLine> lines, Rules rules, CancellationToken ct)
    {
        var auditDir = Path.Combine(input.OutputDir, HermesSpecReader.HermesDir);
        var read = new List<InvoiceReadResult>();
        foreach (var file in input.Invoices)
        {
            read.Add(await invoiceExtractor.ReadAsync(file, options.CompanyName, auditDir, ct));
        }

        var matches = new Queue<InvoiceMatch>(InvoiceMatcher.Match(
            read.Where(r => r.Facts is not null).Select(r => r.Facts!).ToList(), lines, rules));
        var summaries = read.Select(r =>
        {
            if (r.Facts is null)
            {
                return new InvoiceSummary { File = r.File, Reason = r.HoldReason, Errors = r.Errors };
            }

            var match = matches.Dequeue();
            return new InvoiceSummary
            {
                File = r.File,
                Facts = match.Invoice,
                Reason = match.Reason,
                Note = match.Note,
                Candidates = match.Candidates,
            };
        }).ToList();

        foreach (var summary in summaries)
        {
            JobOutputWriter.WriteInvoice(input.OutputDir, summary);
        }

        return summaries;
    }

    /// <summary>
    /// Job-level holds on top of the FR-6 mapping, in order: duplicate lines inside the job, ledger duplicates (G4),
    /// kinds the requirement did not ask for.
    /// </summary>
    private static IReadOnlyList<MappedTxn> ApplyJobGates(IReadOnlyList<MappedTxn> mapped, JobSpec spec, Ledger ledger)
    {
        var fingerprintCounts = mapped
            .GroupBy(m => m.Line.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return mapped.Select(m =>
        {
            if (m.Decision == Decision.Skip)
            {
                return m;
            }

            if (fingerprintCounts[m.Line.Fingerprint] > 1)
            {
                // SPEC-GAP T-103: identical lines share a fingerprint and requestID; hold both rather than post one twice or once.
                return Hold(m, HoldReasons.DuplicateLine, "identical line appears more than once in this job");
            }

            if (ledger.IsPosted(m.Line.Fingerprint))
            {
                return m with { Decision = Decision.Skip, Reason = HoldReasons.AlreadyPosted };
            }

            if ((m.Decision == Decision.Post || ModelTiers.Needs(m)) && !spec.Kinds.Contains(m.Kind))
            {
                // SPEC-GAP T-103: the requirement did not ask for this transaction type.
                return Hold(m, HoldReasons.KindNotRequested, $"{m.Kind} is not in the requirement");
            }

            return m;
        }).ToList();
    }

    private void RecordInLedger(JobRecord job, AnalysisResult analysis, PostVerification verification)
    {
        var store = new LedgerStore(options.LedgerFile);
        var ledger = store.Load();
        var entries = verification.Posted.Select(p => new LedgerEntry
        {
            BatchId = job.BatchId!,
            JobId = job.JobId,
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
            JobId = job.JobId,
            BatchId = job.BatchId!,
            PostedUtc = clock.UtcNow,
            Posted = entries.Count,
            Held = analysis.Lines.Count(l => l.Decision == Decision.Hold) + verification.Rejected.Count,
            Skipped = analysis.Lines.Count(l => l.Decision == Decision.Skip),
            TxnIds = entries.Select(e => e.TxnId).ToList(),
        };

        store.Save(ledger with { Jobs = [.. ledger.Jobs, batch], Posted = [.. ledger.Posted, .. entries] });
    }

    private static MappedTxn Hold(MappedTxn txn, string reason, string note) =>
        txn with { Decision = Decision.Hold, Confidence = Confidence.Hold, Reason = reason, Note = note };
}
