using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Gates;

/// <summary>
/// Gate G4, QuickBooks half (spec FR-8), run only when posting: for each (kind, account) among the postable lines, one
/// query over <c>[minDate − W, maxDate + W]</c>. A line with a same-amount transaction within W days is skipped
/// <c>already-in-quickbooks</c> when the match is exact (same date and the same non-ACH RefNumber or the same payee),
/// otherwise held <c>possible-duplicate</c>. Every query is saved as <c>output/query-&lt;n&gt;.qbxml</c> (FR-17).
/// A gateway failure propagates: nothing has been posted yet.
/// </summary>
public sealed class LiveDuplicateGate(IQbGateway gateway, string qbXmlVersion, int windowDays)
{
    public const string AuditPrefix = "query-";

    public async Task<IReadOnlyList<MappedTxn>> CheckAsync(IReadOnlyList<MappedTxn> lines, string outputDir, CancellationToken ct)
    {
        ClearOldAudit(outputDir);
        var decided = new Dictionary<MappedTxn, MappedTxn>(ReferenceEqualityComparer.Instance);
        var groups = lines
            .Where(l => l.Decision == Decision.Post)
            .GroupBy(l => (l.Kind, Account: l.Account!), new GroupKeyComparer());

        var n = 0;
        foreach (var group in groups)
        {
            n++;
            var from = group.Min(l => l.Line.Date).AddDays(-windowDays);
            var to = group.Max(l => l.Line.Date).AddDays(windowDays);
            var request = QbTxnQuery.Build(group.Key.Kind, group.Key.Account, from, to, qbXmlVersion);
            AtomicFile.WriteAllText(Path.Combine(outputDir, $"{AuditPrefix}{n}.qbxml"), request);

            var response = await gateway.ProcessAsync(request, ct);
            AtomicFile.WriteAllText(Path.Combine(outputDir, $"{AuditPrefix}{n}.response.qbxml"), response);

            IReadOnlyList<ExistingTxn> existing;
            try
            {
                existing = QbTxnQuery.Parse(group.Key.Kind, response)
                    .Where(e => e.Account is null || string.Equals(e.Account, group.Key.Account, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is QbStatusException or FormatException or System.Xml.XmlException)
            {
                // SPEC-GAP T-603: the duplicate check could not be done for this group → hold its lines.
                foreach (var line in group)
                {
                    decided[line] = Hold(line, HoldReasons.DuplicateCheckFailed, $"QuickBooks duplicate query failed: {ex.Message}");
                }

                continue;
            }

            foreach (var line in group)
            {
                decided[line] = Decide(line, existing, windowDays);
            }
        }

        return lines.Select(l => decided.GetValueOrDefault(l, l)).ToList();
    }

    /// <summary>FR-8 decision for one postable line against the transactions QuickBooks returned.</summary>
    public static MappedTxn Decide(MappedTxn line, IReadOnlyList<ExistingTxn> existing, int windowDays)
    {
        var sameAmount = existing
            .Where(e => e.Amount == line.Line.Amount && Math.Abs(e.Date.DayNumber - line.Line.Date.DayNumber) <= windowDays)
            .ToList();
        if (sameAmount.Count == 0)
        {
            return line;
        }

        var exact = sameAmount.FirstOrDefault(e => e.Date == line.Line.Date && (SameRef(line, e) || SamePayee(line, e)));
        if (exact is not null)
        {
            return line with
            {
                Decision = Decision.Skip,
                Reason = HoldReasons.AlreadyInQuickBooks,
                Note = $"QuickBooks TxnID {exact.TxnId}",
            };
        }

        return Hold(
            line,
            HoldReasons.PossibleDuplicate,
            "same amount in QuickBooks within the window: " + string.Join(", ", sameAmount.Select(e => $"TxnID {e.TxnId} on {e.Date:yyyy-MM-dd}")));
    }

    private static bool SameRef(MappedTxn line, ExistingTxn e) =>
        !string.IsNullOrWhiteSpace(line.RefNumber)
        && !string.Equals(line.RefNumber, "ACH", StringComparison.OrdinalIgnoreCase)
        && string.Equals(line.RefNumber, e.RefNumber, StringComparison.OrdinalIgnoreCase);

    // SPEC-GAP T-603: payees compare ignoring case, as QuickBooks names do.
    private static bool SamePayee(MappedTxn line, ExistingTxn e) =>
        !string.IsNullOrWhiteSpace(line.Payee) && string.Equals(line.Payee, e.Payee, StringComparison.OrdinalIgnoreCase);

    private static MappedTxn Hold(MappedTxn txn, string reason, string note) =>
        txn with { Decision = Decision.Hold, Confidence = Confidence.Hold, Reason = reason, Note = note };

    private static void ClearOldAudit(string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(outputDir, AuditPrefix + "*.qbxml"))
        {
            File.Delete(file);
        }
    }

    private sealed class GroupKeyComparer : IEqualityComparer<(TxnKind Kind, string Account)>
    {
        public bool Equals((TxnKind Kind, string Account) x, (TxnKind Kind, string Account) y) =>
            x.Kind == y.Kind && string.Equals(x.Account, y.Account, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((TxnKind Kind, string Account) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Account));
    }
}
