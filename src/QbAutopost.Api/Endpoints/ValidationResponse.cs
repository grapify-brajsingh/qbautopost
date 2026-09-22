using System.Text.Json.Serialization;
using QbAutopost.Core.Api;
using QbAutopost.Core.Models;

namespace QbAutopost.Api.Endpoints;

/// <summary>
/// The FR-A-6 body of <c>POST /api/v1/quickbooks/transactions/validate</c>. It is returned on 200 <i>and</i> on 422:
/// a caller whose batch was refused needs the reasons more than a caller whose batch was fine.
/// </summary>
public sealed record ValidationResponse(
    bool Ok,
    ValidationCounts Counts,
    ValidationTotals Totals,
    IReadOnlyList<ValidationRow> Rows,
    DirectUnknownNames UnknownNames,
    ValidationQbXml QbXml)
{
    /// <summary>
    /// FR-A-6 returns the qbXML body only for <c>?includeQbXml=true</c> from a key holding <c>qb:debug</c> (T-910).
    /// A caller who asks without that scope is told why rather than left wondering where the field went.
    /// </summary>
    public const string DebugScopeNote = "the qbXML request body needs a caller key holding the qb:debug scope";

    /// <param name="includeQbXml">
    /// True only when the caller both asked for the body and holds <c>qb:debug</c>; the endpoint decides that,
    /// because only it can see who is calling.
    /// </param>
    /// <param name="asked">True when the caller asked at all, so a refusal can explain itself.</param>
    public static ValidationResponse From(DirectPlan plan, bool includeQbXml, bool asked = false) => new(
        plan.Ok,
        new ValidationCounts(plan.Submitted, plan.WouldPost, plan.Held, plan.Skipped, plan.Duplicates),
        new ValidationTotals(plan.SubmittedTotal, plan.WouldPostTotal, plan.ControlTotal, plan.Errors.Count == 0),
        [.. plan.Rows.Select(ValidationRow.From)],
        plan.UnknownNames,
        new ValidationQbXml(
            plan.ToPost.Count,
            plan.QbXmlCounts,
            includeQbXml && plan.QbXml.Length > 0 ? plan.QbXml : null,
            asked && !includeQbXml ? DebugScopeNote : null));
}

/// <summary>
/// The four outcomes. <c>skipped</c> includes <c>duplicates</c>, so <c>wouldPost + held + skipped</c> is
/// <c>submitted</c> and a caller can check the arithmetic of the answer as well as of the request.
/// </summary>
public sealed record ValidationCounts(int Submitted, int WouldPost, int Held, int Skipped, int Duplicates);

/// <summary>
/// Money as submitted and money that would post. <paramref name="ControlTotalOk"/> is always true here: a control
/// total that does not match is a 400 (§6.1) and never produces this body, and no amount is ever adjusted to agree.
/// </summary>
public sealed record ValidationTotals(decimal Submitted, decimal WouldPost, decimal? ControlTotal, bool ControlTotalOk);

/// <summary>One submitted row and what would become of it.</summary>
public sealed record ValidationRow(
    int Index,
    string? ExternalId,
    string? RequestId,
    DirectOutcome Outcome,
    TxnKind? Kind,
    string? Account,
    string? LineAccount,
    string? Payee,
    decimal Amount,
    Confidence? Confidence,
    string? Reason,
    string? Note,
    IReadOnlyList<string>? Candidates,
    string? BatchId)
{
    public static ValidationRow From(DirectPlanRow row) => new(
        row.Index,
        row.ExternalId,
        row.RequestId,
        row.Outcome,
        row.Kind,
        row.Account,
        row.LineAccount,
        row.Payee,
        row.Amount,
        row.Confidence,
        row.Reason,
        row.Note,
        row.Candidates.Count == 0 ? null : row.Candidates,
        row.BatchId);
}

/// <summary>
/// What would be sent, by count only. The qbXML itself is never in a response by default and never in a log
/// (spec §14): it carries every amount and name in the batch.
/// </summary>
public sealed record ValidationQbXml(
    int RequestCount,
    IReadOnlyDictionary<string, int> ByType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Request,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note);
