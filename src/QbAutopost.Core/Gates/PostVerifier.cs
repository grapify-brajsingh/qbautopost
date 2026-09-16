using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Gates;

public sealed record PostVerification(IReadOnlyList<PostedLine> Posted, IReadOnlyList<MappedTxn> Rejected);

/// <summary>
/// Gate G5 (spec FR-12): a line is posted iff its <c>*AddRs</c> has statusCode 0, a TxnID, and an echoed amount
/// (when present) equal to the statement amount. Everything else is held with the SDK message.
/// </summary>
public static class PostVerifier
{
    public static PostVerification Verify(IReadOnlyList<MappedTxn> sent, IReadOnlyList<PostResult> results)
    {
        var byRequest = results
            .GroupBy(r => r.RequestId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var posted = new List<PostedLine>();
        var rejected = new List<MappedTxn>();
        foreach (var txn in sent)
        {
            if (!byRequest.TryGetValue(txn.RequestId, out var matches))
            {
                rejected.Add(Hold(txn, HoldReasons.QuickBooksNoResponse, "no response for this requestID"));
                continue;
            }

            if (matches.Count > 1)
            {
                // SPEC-GAP T-103: two responses for one requestID cannot be told apart → hold, a person checks QuickBooks.
                rejected.Add(Hold(txn, HoldReasons.QuickBooksNoResponse, $"{matches.Count} responses for this requestID; check QuickBooks"));
                continue;
            }

            var result = matches[0];
            if (result.StatusCode != 0)
            {
                rejected.Add(Hold(txn, HoldReasons.QuickBooksRejected, $"{result.StatusCode}: {result.StatusMessage}"));
            }
            else if (result.TxnId is null)
            {
                rejected.Add(Hold(txn, HoldReasons.QuickBooksRejected, "status 0 but no TxnID returned"));
            }
            else if (result.Amount is { } echoed && echoed != txn.Line.Amount)
            {
                // The transaction exists in QuickBooks with the wrong amount; the note names it so a person can delete it.
                rejected.Add(Hold(
                    txn,
                    HoldReasons.AmountMismatch,
                    $"QuickBooks TxnID {result.TxnId} echoed {Money.Format(echoed)}, statement says {Money.Format(txn.Line.Amount)}; delete it in QuickBooks"));
            }
            else
            {
                posted.Add(new PostedLine(txn, result.TxnId, result.EditSequence));
            }
        }

        return new PostVerification(posted, rejected);
    }

    private static MappedTxn Hold(MappedTxn txn, string reason, string note) =>
        txn with { Decision = Decision.Hold, Reason = reason, Note = note };
}
