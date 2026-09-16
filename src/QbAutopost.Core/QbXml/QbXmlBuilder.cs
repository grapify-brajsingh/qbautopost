using System.Globalization;
using System.Text;
using System.Xml;
using QbAutopost.Core.Models;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.QbXml;

/// <summary>
/// Builds the qbXML add request (spec FR-9). Element order is fixed by the qbXML schema and guarded by
/// golden files in <c>tests/fixtures/qbxml</c> — change it only together with those files (CLAUDE.md rule 5).
/// </summary>
public static class QbXmlBuilder
{
    public const string DefaultVersion = "13.0";

    public static string BuildAddRequest(IReadOnlyList<MappedTxn> txns, string qbXmlVersion = DefaultVersion)
    {
        foreach (var txn in txns)
        {
            if (txn.Decision != Decision.Post || txn.Kind == TxnKind.Skip)
            {
                throw new ArgumentException($"Line {txn.RequestId} is not postable ({txn.Decision}, {txn.Kind}).", nameof(txns));
            }
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(false),
        };

        using var buffer = new Utf8StringWriter();
        using (var w = XmlWriter.Create(buffer, settings))
        {
            w.WriteStartDocument();
            w.WriteProcessingInstruction("qbxml", $"version=\"{qbXmlVersion}\"");
            w.WriteStartElement("QBXML");
            w.WriteStartElement("QBXMLMsgsRq");
            w.WriteAttributeString("onError", "continueOnError");

            foreach (var txn in txns)
            {
                switch (txn.Kind)
                {
                    case TxnKind.Check:
                        WriteCheck(w, txn);
                        break;
                    case TxnKind.CcCharge:
                        WriteCard(w, txn, "CreditCardChargeAdd");
                        break;
                    case TxnKind.CcCredit:
                        WriteCard(w, txn, "CreditCardCreditAdd");
                        break;
                    case TxnKind.Deposit:
                        WriteDeposit(w, txn);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(txns), txn.Kind, "Unsupported transaction kind.");
                }
            }

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return buffer.ToString() + "\n";
    }

    // CheckAdd(AccountRef, PayeeEntityRef?, RefNumber, TxnDate, Memo, IsToBePrinted=false, ExpenseLineAdd)
    private static void WriteCheck(XmlWriter w, MappedTxn txn)
    {
        StartRequest(w, "CheckAdd", txn);
        WriteRef(w, "AccountRef", Required(txn.Account, txn, nameof(txn.Account)));
        WriteOptionalRef(w, "PayeeEntityRef", txn.Payee);
        WriteText(w, "RefNumber", Required(txn.RefNumber, txn, nameof(txn.RefNumber)));
        WriteDate(w, txn.Line.Date);
        WriteText(w, "Memo", txn.Memo);
        w.WriteElementString("IsToBePrinted", "false");
        WriteExpenseLine(w, txn);
        EndRequest(w);
    }

    // CreditCardChargeAdd|CreditCardCreditAdd(AccountRef, PayeeEntityRef?, TxnDate, RefNumber, Memo, ExpenseLineAdd)
    private static void WriteCard(XmlWriter w, MappedTxn txn, string element)
    {
        StartRequest(w, element, txn);
        WriteRef(w, "AccountRef", Required(txn.Account, txn, nameof(txn.Account)));
        WriteOptionalRef(w, "PayeeEntityRef", txn.Payee);
        WriteDate(w, txn.Line.Date);
        WriteText(w, "RefNumber", Required(txn.RefNumber, txn, nameof(txn.RefNumber)));
        WriteText(w, "Memo", txn.Memo);
        WriteExpenseLine(w, txn);
        EndRequest(w);
    }

    // DepositAdd(TxnDate, DepositToAccountRef, Memo, DepositLineAdd(EntityRef?, AccountRef, Memo, Amount))
    private static void WriteDeposit(XmlWriter w, MappedTxn txn)
    {
        StartRequest(w, "DepositAdd", txn);
        WriteDate(w, txn.Line.Date);
        WriteRef(w, "DepositToAccountRef", Required(txn.Account, txn, nameof(txn.Account)));
        WriteText(w, "Memo", txn.Memo);
        w.WriteStartElement("DepositLineAdd");
        WriteOptionalRef(w, "EntityRef", txn.Payee);
        WriteRef(w, "AccountRef", Required(txn.LineAccount, txn, nameof(txn.LineAccount)));
        WriteText(w, "Memo", txn.Memo);
        w.WriteElementString("Amount", Money.Format(txn.Line.Amount));
        w.WriteEndElement();
        EndRequest(w);
    }

    // ExpenseLineAdd(AccountRef, Amount, Memo)
    private static void WriteExpenseLine(XmlWriter w, MappedTxn txn)
    {
        w.WriteStartElement("ExpenseLineAdd");
        WriteRef(w, "AccountRef", Required(txn.LineAccount, txn, nameof(txn.LineAccount)));
        w.WriteElementString("Amount", Money.Format(txn.Line.Amount));
        WriteText(w, "Memo", txn.Memo);
        w.WriteEndElement();
    }

    private static void StartRequest(XmlWriter w, string element, MappedTxn txn)
    {
        w.WriteStartElement(element + "Rq");
        w.WriteAttributeString("requestID", txn.RequestId);
        w.WriteStartElement(element);
    }

    private static void EndRequest(XmlWriter w)
    {
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteRef(XmlWriter w, string element, string fullName)
    {
        w.WriteStartElement(element);
        WriteText(w, "FullName", fullName);
        w.WriteEndElement();
    }

    private static void WriteOptionalRef(XmlWriter w, string element, string? fullName)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            WriteRef(w, element, fullName);
        }
    }

    private static void WriteDate(XmlWriter w, DateOnly date) =>
        w.WriteElementString("TxnDate", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>Writes escaped text, dropping characters XML cannot carry (control characters from statement text).</summary>
    private static void WriteText(XmlWriter w, string element, string value) =>
        w.WriteElementString(element, new string(value.Where(XmlConvert.IsXmlChar).ToArray()));

    private static string Required(string? value, MappedTxn txn, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Line {txn.RequestId} has no {field}; it should have been held.")
            : value;

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding { get; } = new UTF8Encoding(false);
    }
}
