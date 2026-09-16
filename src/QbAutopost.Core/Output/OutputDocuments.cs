using QbAutopost.Core.Extract;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;

namespace QbAutopost.Core.Output;

/// <summary><c>output/spec.json</c>: T1 (or regex) result plus gate G2 (spec §5, FR-2).</summary>
public sealed record SpecDocument(string Source, string Company, JobSpec Spec, SpecGateResult Gate, string? Note);

/// <summary><c>output/statements/&lt;file&gt;.rows.json</c> (spec FR-3).</summary>
public sealed record RowsDocument
{
    public required string File { get; init; }
    public string? Last4 { get; init; }
    public SourceKind? Kind { get; init; }
    public string? Layout { get; init; }
    public IReadOnlyList<StatementLine> Rows { get; init; } = [];
    public ReconcileResult? Reconcile { get; init; }

    /// <summary>Printed statement totals (T2 only).</summary>
    public StatementTotals? Totals { get; init; }
    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static RowsDocument From(StatementSummary s) => new()
    {
        File = s.File,
        Last4 = s.Last4,
        Kind = s.Kind,
        Layout = s.Layout,
        Rows = s.Lines,
        Reconcile = s.Reconcile,
        Totals = s.Totals,
        HoldReason = s.HoldReason,
        Errors = s.Errors,
    };
}

/// <summary><c>output/analysis.json</c> (spec §10).</summary>
public sealed record AnalysisDocument(string JobId, string Company, JobSpec Spec, IReadOnlyList<AnalysisLine> Lines);

public sealed record AnalysisLine
{
    public required string RequestId { get; init; }
    public required string File { get; init; }
    public int LineNo { get; init; }
    public DateOnly Date { get; init; }
    public required string Description { get; init; }
    public decimal Amount { get; init; }
    public Direction Direction { get; init; }
    public TxnKind Kind { get; init; }
    public string? Account { get; init; }
    public string? Payee { get; init; }
    public string? LineAccount { get; init; }
    public string? RefNumber { get; init; }
    public int? Tier { get; init; }
    public Confidence Confidence { get; init; }

    /// <summary>
    /// Hermes T4 score (0…1) when tier 3 or 4 asked the model, else null.
    /// SPEC-GAP T-503: §10 names one <c>confidence</c> field; it keeps the FR-6 category (rule, history, invoice,
    /// model, holding, hold) and the score is written next to it, so G3 decisions can be reviewed.
    /// </summary>
    public double? ModelConfidence { get; init; }

    public string? Reason { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = [];
    public string? InvoiceRef { get; init; }
    public Decision Decision { get; init; }

    public static AnalysisLine From(MappedTxn t) => new()
    {
        RequestId = t.RequestId,
        File = t.Line.SourceFile,
        LineNo = t.Line.LineNo,
        Date = t.Line.Date,
        Description = t.Line.Description,
        Amount = t.Line.Amount,
        Direction = t.Line.Direction,
        Kind = t.Kind,
        Account = t.Account,
        Payee = t.Payee,
        LineAccount = t.LineAccount,
        RefNumber = t.RefNumber,
        Tier = t.Tier,
        Confidence = t.Confidence,
        ModelConfidence = t.ModelConfidence,
        Reason = t.Reason,
        Note = t.Note,
        Candidates = t.Candidates,
        InvoiceRef = t.InvoiceRef,
        Decision = t.Decision,
    };
}

/// <summary><c>output/result.json</c> (spec §10). Also the source of <c>GET /jobs/{id}</c> after a restart.</summary>
public sealed record ResultDocument
{
    public required string JobId { get; init; }
    public string? BatchId { get; init; }
    public required JobStatus Status { get; init; }
    public bool DryRun { get; init; }
    public string? Company { get; init; }
    public string? Error { get; init; }
    public ResultCounts Counts { get; init; } = new(0, 0, 0, 0);
    public ResultTotals Totals { get; init; } = new(0m, 0m);
    public IReadOnlyList<PostedItem> Posted { get; init; } = [];
    public IReadOnlyList<HeldItem> Held { get; init; } = [];
    public IReadOnlyList<SkippedItem> Skipped { get; init; } = [];
    public IReadOnlyList<UnreadableFile> Unreadable { get; init; } = [];

    /// <summary>
    /// FR-5: invoices that matched no line, including files that could not be read (SPEC-GAP T-402: reported here,
    /// with their hold code as <c>reason</c>, rather than in <see cref="Unreadable"/>, which lists skipped folder entries).
    /// </summary>
    public IReadOnlyList<InvoiceSummary> UnmatchedInvoices { get; init; } = [];

    public IReadOnlyList<StatementSummary> Reconcile { get; init; } = [];
    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }

    /// <summary>
    /// Builds the document from whatever the run produced. <paramref name="analysis"/> is null when the run
    /// crashed before analysis finished; <paramref name="outcome"/> is null for dry runs and failed analyses.
    /// </summary>
    public static ResultDocument Build(
        JobRecord job, AnalysisResult? analysis, PostOutcome? outcome, DateTime startedUtc, DateTime finishedUtc)
    {
        analysis ??= outcome?.Analysis;
        var lines = analysis is { Gate.Ok: true } ? analysis.Lines : [];
        var toPost = lines.Where(l => l.Decision == Decision.Post).ToList();
        var rejected = outcome?.Rejected ?? [];
        var held = lines.Where(l => l.Decision == Decision.Hold).Concat(rejected).ToList();
        var skipped = lines.Where(l => l.Decision == Decision.Skip).ToList();
        var posted = outcome?.Posted ?? [];

        return new ResultDocument
        {
            JobId = job.JobId,
            BatchId = job.BatchId,
            Status = job.Status,
            DryRun = job.DryRun,
            Company = analysis?.Company,
            Error = job.Error,
            Counts = new ResultCounts(toPost.Count, posted.Count, held.Count, skipped.Count),
            Totals = new ResultTotals(toPost.Sum(t => t.Line.Amount), posted.Sum(p => p.Txn.Line.Amount)),
            Posted = posted.Select(PostedItem.From).ToList(),
            Held = held.Select(HeldItem.From).ToList(),
            Skipped = skipped.Select(SkippedItem.From).ToList(),
            Unreadable = analysis?.Input.Unreadable ?? [],
            UnmatchedInvoices = analysis?.Invoices.Where(i => !i.Matched).ToList() ?? [],
            Reconcile = analysis?.Statements ?? [],
            StartedUtc = startedUtc,
            FinishedUtc = finishedUtc,
        };
    }
}

/// <summary><see cref="ToPost"/> = lines that passed every gate (sent to QuickBooks when posting).</summary>
public sealed record ResultCounts(int ToPost, int Posted, int Held, int Skipped);

public sealed record ResultTotals(decimal ToPost, decimal Posted);

public sealed record PostedItem(
    string RequestId, string TxnId, TxnKind Kind, string? Account, string? Payee, string? LineAccount, decimal Amount, DateOnly Date)
{
    public static PostedItem From(PostedLine p) => new(
        p.Txn.RequestId, p.TxnId, p.Txn.Kind, p.Txn.Account, p.Txn.Payee, p.Txn.LineAccount, p.Txn.Line.Amount, p.Txn.Line.Date);
}

public sealed record HeldItem(
    string RequestId, string File, int LineNo, DateOnly Date, decimal Amount, string Description, TxnKind Kind,
    string? Reason, string? Note, IReadOnlyList<string> Candidates)
{
    public static HeldItem From(MappedTxn t) => new(
        t.RequestId, t.Line.SourceFile, t.Line.LineNo, t.Line.Date, t.Line.Amount, t.Line.Description, t.Kind,
        t.Reason, t.Note, t.Candidates);
}

/// <summary><see cref="Note"/> names the QuickBooks TxnID for <c>already-in-quickbooks</c> (T-603).</summary>
public sealed record SkippedItem(string RequestId, string File, int LineNo, string? Reason, string? Note = null)
{
    public static SkippedItem From(MappedTxn t) => new(t.RequestId, t.Line.SourceFile, t.Line.LineNo, t.Reason, t.Note);
}
