using QbAutopost.Core.Extract;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Pipeline;

/// <summary>
/// One parsed statement turned into a <see cref="StatementSummary"/> and put through gate G1 (spec FR-4).
/// <para>
/// T-914: this used to live inside <see cref="JobPipeline"/>. Folder validation (FR-A-7) has to reach the same
/// verdict as the job, so both call this rather than each holding their own copy of "which reconcile check applies
/// and what a failure means" — two copies would eventually disagree, and the one that disagreed quietly would be
/// the validate that told an operator a statement was fine.
/// </para>
/// </summary>
public static class StatementCheck
{
    /// <summary>
    /// FR-4: Hermes T2 output is checked against the statement's own printed totals, CSV/XLSX against the balance
    /// column. A statement that fails is held whole — a half-trusted statement posts nothing.
    /// </summary>
    public static StatementSummary Reconcile(StatementParseResult parsed, JobFile file)
    {
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

        var reconcile = parsed.Totals is { } totals
            ? ReconcileGate.CheckExtraction(parsed.Kind!.Value, parsed.Rows, totals)
            : ReconcileGate.CheckBalanceChain(parsed.Rows);
        summary = summary with { Reconcile = reconcile };
        return reconcile.Ok ? summary : summary with { HoldReason = HoldReasons.ReconcileFailed };
    }
}
