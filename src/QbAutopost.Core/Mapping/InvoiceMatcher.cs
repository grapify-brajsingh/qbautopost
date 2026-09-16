using System.Globalization;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Mapping;

/// <summary>
/// One invoice after matching. <see cref="InvoiceFacts.MatchedRequestId"/> is set when it matched; otherwise
/// <see cref="Reason"/> says why. <see cref="Candidates"/> are the request ids of every line within amount and date range.
/// </summary>
public sealed record InvoiceMatch(InvoiceFacts Invoice, string? Reason, string? Note, IReadOnlyList<string> Candidates)
{
    public bool Matched => Invoice.MatchedRequestId is not null;
}

/// <summary>
/// Invoice → statement line (spec FR-5). Candidates: <c>|amount − total| ≤ 0.005</c> and <c>|days| ≤ InvoiceMatchDays</c>.
/// One candidate matches. Several: same direction first (vendor ↔ debit, customer ↔ credit), then the single best
/// payee similarity ≥ <c>FuzzyThreshold</c>; anything else is <c>ambiguous</c>. Amounts are only compared, never changed.
/// </summary>
public static class InvoiceMatcher
{
    public const decimal AmountTolerance = 0.005m;

    public static IReadOnlyList<InvoiceMatch> Match(IReadOnlyList<InvoiceFacts> invoices, IReadOnlyList<StatementLine> lines, Rules rules)
    {
        var matches = invoices.Select(i => MatchOne(i, lines, rules)).ToList();

        // SPEC-GAP T-402: a line claimed by several invoices is evidence for none of them.
        var claims = matches
            .Where(m => m.Matched)
            .GroupBy(m => m.Invoice.MatchedRequestId!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Invoice.File).ToList(), StringComparer.Ordinal);

        return matches.Select(m =>
        {
            if (!m.Matched || !claims.TryGetValue(m.Invoice.MatchedRequestId!, out var files))
            {
                return m;
            }

            var others = string.Join(", ", files.Where(f => f != m.Invoice.File));
            return m with
            {
                Invoice = m.Invoice with { MatchedRequestId = null },
                Reason = HoldReasons.Ambiguous,
                Note = $"line {m.Invoice.MatchedRequestId} is also matched by {others}",
            };
        }).ToList();
    }

    private static InvoiceMatch MatchOne(InvoiceFacts invoice, IReadOnlyList<StatementLine> lines, Rules rules)
    {
        if (invoice.Date is not { } date)
        {
            // SPEC-GAP T-402: without a date the FR-5 day window cannot be checked.
            return new InvoiceMatch(invoice, HoldReasons.NoInvoiceDate, "the invoice shows no date", []);
        }

        var candidates = lines
            .Where(l => Math.Abs(l.Amount - invoice.Total) <= AmountTolerance
                        && Math.Abs(l.Date.DayNumber - date.DayNumber) <= rules.InvoiceMatchDays)
            .ToList();
        var ids = candidates.Select(l => l.RequestId).ToList();
        if (candidates.Count == 0)
        {
            return new InvoiceMatch(
                invoice,
                HoldReasons.NoMatchingLine,
                string.Create(CultureInfo.InvariantCulture, $"no line of {invoice.Total:0.00} within {rules.InvoiceMatchDays} days of {date:yyyy-MM-dd}"),
                ids);
        }

        if (candidates.Count == 1)
        {
            return Matched(invoice, candidates[0], ids);
        }

        // SPEC-GAP T-402: when no candidate has the preferred direction, all candidates stay in the running.
        var preferred = invoice.Role == PartyRole.Vendor ? Direction.Debit : Direction.Credit;
        var pool = candidates.Where(l => l.Direction == preferred).ToList();
        if (pool.Count == 0)
        {
            pool = candidates;
        }

        if (pool.Count == 1)
        {
            return Matched(invoice, pool[0], ids);
        }

        // SPEC-GAP T-402: the line's payee is not resolved yet, so similarity is party name vs line description.
        var scored = pool
            .Select(l => (Line: l, Score: Fuzzy.MatchScore(l.Description, invoice.Party)))
            .Where(s => s.Score >= rules.FuzzyThreshold)
            .ToList();
        if (scored.Count == 0)
        {
            return new InvoiceMatch(
                invoice,
                HoldReasons.Ambiguous,
                string.Create(CultureInfo.InvariantCulture, $"{pool.Count} candidate lines and none resembles \"{invoice.Party}\" (similarity < {rules.FuzzyThreshold})"),
                ids);
        }

        var top = scored.Max(s => s.Score);
        var best = scored.Where(s => s.Score == top).ToList();
        return best.Count == 1
            ? Matched(invoice, best[0].Line, ids)
            : new InvoiceMatch(invoice, HoldReasons.Ambiguous, $"{best.Count} candidate lines resemble \"{invoice.Party}\" equally", ids);
    }

    private static InvoiceMatch Matched(InvoiceFacts invoice, StatementLine line, IReadOnlyList<string> candidates) =>
        new(invoice with { MatchedRequestId = line.RequestId }, null, null, candidates);
}
