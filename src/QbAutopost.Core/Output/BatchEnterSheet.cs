using System.Globalization;
using System.Text;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Output;

/// <summary>
/// Paste-ready sheets for QuickBooks <i>Batch Enter Transactions</i> (spec FR-9, §10), written even in dry run.
/// SPEC-GAP T-002: the POC column set was unavailable; columns mirror the Batch Enter grids and dates are ISO
/// (CLAUDE.md convention) — see tracker Questions. Only lines with decision <c>Post</c> are included.
/// </summary>
public static class BatchEnterSheet
{
    public const string ChecksFile = "batch-enter-checks.csv";
    public const string CreditCardFile = "batch-enter-creditcard.csv";
    public const string DepositsFile = "batch-enter-deposits.csv";

    private const string NewLine = "\r\n";

    // Excel needs the BOM to open UTF-8 CSV correctly.
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(true);

    public static string BuildChecks(IEnumerable<MappedTxn> txns) =>
        Build(
            CsvText.Row("Bank", "Date", "Number", "Payee", "Account", "Amount", "Memo"),
            Postable(txns, TxnKind.Check).Select(t => CsvText.Row(
                CsvText.Text(t.Account), Date(t), CsvText.Text(t.RefNumber), CsvText.Text(t.Payee),
                CsvText.Text(t.LineAccount), Amount(t), CsvText.Text(t.Memo))));

    public static string BuildCreditCard(IEnumerable<MappedTxn> txns) =>
        Build(
            CsvText.Row("Card", "Date", "Number", "Payee", "Type", "Account", "Amount", "Memo"),
            txns
                .Where(t => t.Decision == Decision.Post && t.Kind is TxnKind.CcCharge or TxnKind.CcCredit)
                .Select(t => CsvText.Row(
                    CsvText.Text(t.Account), Date(t), CsvText.Text(t.RefNumber), CsvText.Text(t.Payee),
                    CsvText.Raw(t.Kind == TxnKind.CcCharge ? "Charge" : "Credit"),
                    CsvText.Text(t.LineAccount), Amount(t), CsvText.Text(t.Memo))));

    public static string BuildDeposits(IEnumerable<MappedTxn> txns) =>
        Build(
            CsvText.Row("Date", "Received From", "From Account", "Memo", "Amount", "Deposit To"),
            Postable(txns, TxnKind.Deposit).Select(t => CsvText.Row(
                Date(t), CsvText.Text(t.Payee), CsvText.Text(t.LineAccount), CsvText.Text(t.Memo),
                Amount(t), CsvText.Text(t.Account))));

    /// <summary>Writes all three sheets (header-only when a kind has no lines) into <paramref name="outputDir"/>.</summary>
    public static void WriteAll(string outputDir, IReadOnlyList<MappedTxn> txns)
    {
        AtomicFile.WriteAllText(Path.Combine(outputDir, ChecksFile), BuildChecks(txns), Utf8WithBom);
        AtomicFile.WriteAllText(Path.Combine(outputDir, CreditCardFile), BuildCreditCard(txns), Utf8WithBom);
        AtomicFile.WriteAllText(Path.Combine(outputDir, DepositsFile), BuildDeposits(txns), Utf8WithBom);
    }

    private static IEnumerable<MappedTxn> Postable(IEnumerable<MappedTxn> txns, TxnKind kind) =>
        txns.Where(t => t.Decision == Decision.Post && t.Kind == kind);

    private static string Build(string header, IEnumerable<string> rows) =>
        string.Join(NewLine, rows.Prepend(header)) + NewLine;

    private static string Date(MappedTxn t) =>
        CsvText.Raw(t.Line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string Amount(MappedTxn t) => CsvText.Raw(Money.Format(t.Line.Amount));
}
