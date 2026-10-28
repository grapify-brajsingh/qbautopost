using System.Globalization;
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
    /// <summary>The note on a <c>quickbooks-rejected</c> row whose add answered status 0 without a TxnID.</summary>
    public const string NoTxnIdNote = "status 0 but no TxnID returned";

    /// <summary>
    /// The note on a <c>quickbooks-rejected</c> row whose add answered a non-zero status: <c>"&lt;code&gt;: &lt;message&gt;"</c>.
    /// When QuickBooks returned a TxnID anyway (a warning, not an error), the note leads with it instead, so
    /// <see cref="IsCleanRejection"/> can never read that row as "nothing was created".
    /// </summary>
    public static string RejectionNote(int statusCode, string? message, string? txnId = null) => txnId is null
        ? $"{statusCode.ToString(CultureInfo.InvariantCulture)}: {message}"
        : $"QuickBooks TxnID {txnId} returned with {statusCode.ToString(CultureInfo.InvariantCulture)}: {message}; check QuickBooks";

    /// <summary>
    /// R31: true only for a note <see cref="RejectionNote"/> wrote for an add QuickBooks refused outright: a positive
    /// status code (QuickBooks' own error codes are positive; <see cref="QbXml.QbXmlParser.MissingStatusCode"/> is
    /// not) and no TxnID. Every other note, the status-0 one included, is false.
    /// </summary>
    public static bool IsCleanRejection(string? note)
    {
        if (note is null)
        {
            return false;
        }

        var colon = note.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0
            && int.TryParse(
                note.AsSpan(0, colon),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var code)
            && code > 0;
    }

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
                rejected.Add(Hold(txn, HoldReasons.QuickBooksRejected, RejectionNote(result.StatusCode, result.StatusMessage, result.TxnId)));
            }
            else if (result.TxnId is null)
            {
                rejected.Add(Hold(txn, HoldReasons.QuickBooksRejected, NoTxnIdNote));
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
