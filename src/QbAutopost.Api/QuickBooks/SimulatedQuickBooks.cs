using System.Globalization;
using System.Xml.Linq;
using QbAutopost.Core.Models;

namespace QbAutopost.Api.QuickBooks;

/// <summary>A transaction held by <see cref="SimulatedQuickBooks"/>. <see cref="Type"/> is the qbXML name, e.g. <c>Check</c>.</summary>
public sealed record SimulatedTxn
{
    public required string Type { get; init; }
    public required string TxnId { get; init; }
    public string EditSequence { get; init; } = "1";
    public required string Account { get; init; }
    public string? Payee { get; init; }
    public required DateOnly Date { get; init; }
    public string? RefNumber { get; init; }
    public required decimal Amount { get; init; }
}

/// <summary>
/// An in-memory QuickBooks company that answers qbXML the way the SDK does, for the fake gateway in tests (spec §15) and
/// for <c>QuickBooks:Fake=true</c> in Development. It understands the requests this app sends: transaction adds, the
/// G4 transaction queries, list queries, <c>HostQuery</c> and <c>TxnDel</c>. Echoed amounts are the requested ones.
/// </summary>
public sealed class SimulatedQuickBooks(string txnIdPrefix = "SIM-")
{
    public const int NoMatchStatusCode = 1;
    public const int NotFoundStatusCode = 3120;
    public const int UnsupportedStatusCode = 3000;

    private static readonly string[] TxnTypes = ["Check", "CreditCardCharge", "CreditCardCredit", "Deposit"];

    private readonly object _gate = new();
    private readonly List<SimulatedTxn> _txns = [];
    private int _nextTxn;

    public IList<QbAccount> Accounts { get; } = new List<QbAccount>();
    public IList<string> Vendors { get; } = new List<string>();
    public IList<string> Customers { get; } = new List<string>();
    public string ProductName { get; set; } = "QuickBooks Desktop (simulated)";

    public IReadOnlyList<SimulatedTxn> Transactions
    {
        get
        {
            lock (_gate)
            {
                return _txns.ToList();
            }
        }
    }

    /// <summary>Seeds a transaction that "already exists" in QuickBooks (e.g. entered by hand).</summary>
    public void AddExisting(SimulatedTxn txn)
    {
        lock (_gate)
        {
            _txns.Add(txn);
        }
    }

    /// <summary>
    /// Answers one message set. <paramref name="overrideResponse"/> may replace the answer for a request, given its
    /// 1-based position; returning null keeps the normal answer.
    /// </summary>
    public string Process(string qbxml, Func<int, XElement, XElement?>? overrideResponse = null)
    {
        var requests = XDocument.Parse(qbxml).Root?.Element("QBXMLMsgsRq")?.Elements().ToList()
            ?? throw new FormatException("qbXML request has no QBXMLMsgsRq element.");
        var responses = new XElement("QBXMLMsgsRs");
        lock (_gate)
        {
            for (var i = 0; i < requests.Count; i++)
            {
                responses.Add(overrideResponse?.Invoke(i + 1, requests[i]) ?? Handle(requests[i]));
            }
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("QBXML", responses)).ToString();
    }

    /// <summary>A response element for <paramref name="request"/> with the given status and children.</summary>
    public static XElement Response(XElement request, int statusCode, string message, params object?[] content)
    {
        var name = request.Name.LocalName;
        var rs = new XElement(
            name[..^2] + "Rs",
            new XAttribute("statusCode", statusCode),
            new XAttribute("statusSeverity", statusCode == 0 || statusCode == NoMatchStatusCode ? "Info" : "Error"),
            new XAttribute("statusMessage", message));
        if (request.Attribute("requestID") is { } id)
        {
            rs.Add(new XAttribute("requestID", id.Value));
        }

        rs.Add(content);
        return rs;
    }

    private XElement Handle(XElement rq)
    {
        var name = rq.Name.LocalName;
        foreach (var type in TxnTypes)
        {
            if (name == type + "AddRq")
            {
                return Add(rq, type);
            }

            if (name == type + "QueryRq")
            {
                return QueryTxns(rq, type);
            }
        }

        return name switch
        {
            "AccountQueryRq" => QueryList(rq, Accounts.Select(a => new XElement(
                "AccountRet",
                new XElement("Name", a.Name),
                new XElement("FullName", a.Name),
                new XElement("IsActive", "true"),
                a.Type is null ? null : new XElement("AccountType", a.Type)))),
            "VendorQueryRq" => QueryList(rq, Vendors.Select(v => new XElement("VendorRet", new XElement("Name", v), new XElement("IsActive", "true")))),
            "CustomerQueryRq" => QueryList(rq, Customers.Select(c => new XElement(
                "CustomerRet", new XElement("Name", c), new XElement("FullName", c), new XElement("IsActive", "true")))),
            "HostQueryRq" => Response(rq, 0, "Status OK", new XElement(
                "HostRet", new XElement("ProductName", ProductName), new XElement("MajorVersion", "33"), new XElement("MinorVersion", "0"))),
            "TxnDelRq" => Delete(rq),
            _ => Response(rq, UnsupportedStatusCode, $"{name} is not supported by the simulator"),
        };
    }

    private XElement Add(XElement rq, string type)
    {
        var add = rq.Element(type + "Add")!;
        var isDeposit = type == "Deposit";
        var line = isDeposit ? add.Element("DepositLineAdd")! : add.Element("ExpenseLineAdd")!;
        var txn = new SimulatedTxn
        {
            Type = type,
            TxnId = $"{txnIdPrefix}{++_nextTxn}",
            Account = add.Element(isDeposit ? "DepositToAccountRef" : "AccountRef")!.Element("FullName")!.Value,
            Payee = (isDeposit ? line.Element("EntityRef") : add.Element("PayeeEntityRef"))?.Element("FullName")?.Value,
            Date = DateOnly.ParseExact(add.Element("TxnDate")!.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            RefNumber = add.Element("RefNumber")?.Value,
            Amount = decimal.Parse(line.Element("Amount")!.Value, NumberStyles.Number, CultureInfo.InvariantCulture),
        };
        _txns.Add(txn);
        return Response(rq, 0, "Status OK", Ret(txn, includeLines: true));
    }

    private XElement QueryTxns(XElement rq, string type)
    {
        var range = rq.Element("TxnDateRangeFilter");
        var from = ParseDate(range?.Element("FromTxnDate")?.Value) ?? DateOnly.MinValue;
        var to = ParseDate(range?.Element("ToTxnDate")?.Value) ?? DateOnly.MaxValue;
        var account = rq.Element("AccountFilter")?.Element("FullName")?.Value;
        var includeLines = string.Equals(rq.Element("IncludeLineItems")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        var found = _txns
            .Where(t => t.Type == type && t.Date >= from && t.Date <= to)
            .Where(t => account is null || string.Equals(t.Account, account, StringComparison.OrdinalIgnoreCase))
            .Select(t => Ret(t, includeLines))
            .ToList();
        return found.Count == 0
            ? Response(rq, NoMatchStatusCode, "A query request did not find a matching object in QuickBooks")
            : Response(rq, 0, "Status OK", found);
    }

    private static XElement QueryList(XElement rq, IEnumerable<XElement> rets)
    {
        var list = rets.ToList();
        return list.Count == 0
            ? Response(rq, NoMatchStatusCode, "A query request did not find a matching object in QuickBooks")
            : Response(rq, 0, "Status OK", list);
    }

    private XElement Delete(XElement rq)
    {
        var type = rq.Element("TxnDelType")?.Value ?? "";
        var txnId = rq.Element("TxnID")?.Value ?? "";
        var index = _txns.FindIndex(t => t.Type == type && t.TxnId == txnId);
        if (index < 0)
        {
            return Response(rq, NotFoundStatusCode, $"The \"{txnId}\" object ID in the field \"TxnID\" is invalid.");
        }

        _txns.RemoveAt(index);
        return Response(
            rq,
            0,
            "Status OK",
            new XElement("TxnDelType", type),
            new XElement("TxnID", txnId),
            new XElement("TimeDeleted", "2026-09-17T10:00:00"));
    }

    private static XElement Ret(SimulatedTxn t, bool includeLines)
    {
        var date = t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var amount = t.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        if (t.Type == "Deposit")
        {
            return new XElement(
                "DepositRet",
                new XElement("TxnID", t.TxnId),
                new XElement("EditSequence", t.EditSequence),
                new XElement("TxnDate", date),
                Ref("DepositToAccountRef", t.Account),
                new XElement("DepositTotal", amount),
                includeLines ? new XElement("DepositLineRet", Ref("EntityRef", t.Payee), new XElement("Amount", amount)) : null);
        }

        return new XElement(
            t.Type + "Ret",
            new XElement("TxnID", t.TxnId),
            new XElement("EditSequence", t.EditSequence),
            Ref("AccountRef", t.Account),
            Ref("PayeeEntityRef", t.Payee),
            t.RefNumber is null ? null : new XElement("RefNumber", t.RefNumber),
            new XElement("TxnDate", date),
            new XElement("Amount", amount));
    }

    private static XElement? Ref(string element, string? fullName) =>
        fullName is null ? null : new XElement(element, new XElement("FullName", fullName));

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
