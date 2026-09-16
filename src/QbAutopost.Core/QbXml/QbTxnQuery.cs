using System.Globalization;
using System.Xml.Linq;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.QbXml;

/// <summary>A transaction that already exists in QuickBooks, as returned by a G4 query.</summary>
public sealed record ExistingTxn
{
    public required string TxnId { get; init; }
    public required DateOnly Date { get; init; }
    public required decimal Amount { get; init; }
    public string? Account { get; init; }
    public string? Payee { get; init; }
    public string? RefNumber { get; init; }
}

/// <summary>
/// FR-8 gate G4 query: one <c>CheckQuery</c> / <c>CreditCardChargeQuery</c> / <c>CreditCardCreditQuery</c> /
/// <c>DepositQuery</c> per (kind, account) over a date range, and the reader for its answer. Element order is pinned by
/// <c>tests/fixtures/qbxml/txn-query-*.golden.xml</c>.
/// </summary>
public static class QbTxnQuery
{
    public static string QbName(TxnKind kind) => kind switch
    {
        TxnKind.Check => "Check",
        TxnKind.CcCharge => "CreditCardCharge",
        TxnKind.CcCredit => "CreditCardCredit",
        TxnKind.Deposit => "Deposit",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a QuickBooks transaction kind."),
    };

    // <X>QueryRq(TxnDateRangeFilter(FromTxnDate, ToTxnDate), AccountFilter(FullName), IncludeLineItems?)
    public static string Build(TxnKind kind, string account, DateOnly from, DateOnly to, string qbXmlVersion = QbXmlBuilder.DefaultVersion) =>
        QbMessageSet.Build(qbXmlVersion, "stopOnError", w =>
        {
            w.WriteStartElement(QbName(kind) + "QueryRq");
            w.WriteAttributeString("requestID", "1");
            w.WriteStartElement("TxnDateRangeFilter");
            QbMessageSet.WriteDate(w, "FromTxnDate", from);
            QbMessageSet.WriteDate(w, "ToTxnDate", to);
            w.WriteEndElement();
            w.WriteStartElement("AccountFilter");
            QbMessageSet.WriteText(w, "FullName", account);
            w.WriteEndElement();
            if (kind == TxnKind.Deposit)
            {
                // A deposit's customer is on its line, not the header.
                w.WriteElementString("IncludeLineItems", "true");
            }

            w.WriteEndElement();
        });

    /// <summary>Status 1 (no match) → empty; any other non-zero status → <see cref="QbStatusException"/>.</summary>
    public static IReadOnlyList<ExistingTxn> Parse(TxnKind kind, string qbxml)
    {
        var name = QbName(kind);
        var response = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRs")?.Element(name + "QueryRs")
            ?? throw new QbStatusException($"{name}QueryRs is missing from the QuickBooks response");
        var status = QbStatus.Of(response);
        if (status.Code == QbListQuery.NoMatchStatusCode)
        {
            return [];
        }

        if (status.Code != 0)
        {
            throw new QbStatusException($"{name}QueryRs failed: {status}");
        }

        return response.Elements(name + "Ret").Select(ret => Read(kind, ret)).ToList();
    }

    private static ExistingTxn Read(TxnKind kind, XElement ret)
    {
        var deposit = kind == TxnKind.Deposit;
        var amountText = ret.Element(deposit ? "DepositTotal" : "Amount")?.Value;
        return new ExistingTxn
        {
            TxnId = ret.Element("TxnID")?.Value ?? throw new FormatException("query result without TxnID"),
            Date = DateOnly.ParseExact(ret.Element("TxnDate")?.Value ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Amount = decimal.Parse(amountText ?? throw new FormatException("query result without amount"), NumberStyles.Number, CultureInfo.InvariantCulture),
            Account = FullName(ret.Element(deposit ? "DepositToAccountRef" : "AccountRef")),
            Payee = deposit
                ? ret.Elements("DepositLineRet").Select(l => FullName(l.Element("EntityRef"))).FirstOrDefault(p => p is not null)
                : FullName(ret.Element("PayeeEntityRef")),
            RefNumber = ret.Element("RefNumber")?.Value,
        };
    }

    private static string? FullName(XElement? reference) =>
        reference?.Element("FullName")?.Value is { Length: > 0 } name ? name : null;
}
