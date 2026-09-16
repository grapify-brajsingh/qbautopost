using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

/// <summary>
/// Applies the spec FR-6 routing table. Every line gets exactly one decision: post, hold (with a reason)
/// or skip. Nothing here computes an amount — <see cref="StatementLine.Amount"/> passes through untouched.
/// </summary>
public sealed class Mapper
{
    public const string AchRef = "ACH";
    public const int MaxRefNumberLength = 11;

    private readonly Rules _rules;
    private readonly PayeeResolver _payees;
    private readonly LineAccountTiers _tiers;
    private readonly Dictionary<string, InvoiceFacts> _invoices;

    /// <param name="invoices">FR-5 invoices; only those with a <see cref="InvoiceFacts.MatchedRequestId"/> are used.</param>
    public Mapper(
        Rules rules, QbLists? lists = null, IReadOnlyList<LedgerEntry>? history = null, IEnumerable<InvoiceFacts>? invoices = null)
    {
        _rules = rules;
        history ??= [];
        _payees = new PayeeResolver(rules, lists ?? QbLists.Empty, history);
        _tiers = new LineAccountTiers(rules, history);

        _invoices = InvoicesByLine(invoices ?? []);
    }

    /// <summary>Matched invoices by request id. The matcher gives each line at most one; should two arrive anyway, the line gets neither.</summary>
    public static Dictionary<string, InvoiceFacts> InvoicesByLine(IEnumerable<InvoiceFacts> invoices) =>
        invoices
            .Where(i => i.MatchedRequestId is not null)
            .GroupBy(i => i.MatchedRequestId!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    public IReadOnlyList<MappedTxn> MapAll(IEnumerable<StatementLine> lines) => lines.Select(Map).ToList();

    /// <summary>FR-6 routing; a matched invoice's file name is recorded as <see cref="MappedTxn.InvoiceRef"/> whatever the decision.</summary>
    public MappedTxn Map(StatementLine line)
    {
        var txn = line.Kind == SourceKind.Card ? MapCard(line) : MapBank(line);
        return _invoices.TryGetValue(line.RequestId, out var invoice) ? txn with { InvoiceRef = invoice.File } : txn;
    }

    private MappedTxn MapCard(StatementLine line)
    {
        var description = TextNormalizer.Normalize(line.Description);
        var skip = _rules.SkipPatterns.FirstOrDefault(p => TextNormalizer.ContainsFragment(description, p));
        if (skip is not null)
        {
            return new MappedTxn
            {
                Line = line, Kind = TxnKind.Skip, Decision = Decision.Skip, Confidence = Confidence.Rule,
                Reason = $"skip-pattern: {skip}",
            };
        }

        var draft = new MappedTxn
        {
            Line = line,
            Kind = line.Direction == Direction.Debit ? TxnKind.CcCharge : TxnKind.CcCredit,
            Decision = Decision.Post,
            Confidence = Confidence.Rule,
            Account = Lookup(_rules.CardAccounts, line.Last4),
            RefNumber = AchRef,
        };

        return draft.Account is null ? Hold(draft, HoldReasons.UnknownAccount) : WithVendorAndTiers(draft, description);
    }

    private MappedTxn MapBank(StatementLine line)
    {
        var description = TextNormalizer.Normalize(line.Description);
        var account = Lookup(_rules.BankAccounts, line.Last4);
        var draft = new MappedTxn
        {
            Line = line,
            Kind = line.Direction == Direction.Credit ? TxnKind.Deposit : TxnKind.Check,
            Decision = Decision.Post,
            Confidence = Confidence.Rule,
            Account = account,
        };

        if (account is null)
        {
            return Hold(draft, HoldReasons.UnknownAccount);
        }

        if (line.Direction == Direction.Credit)
        {
            return MapDeposit(draft);
        }

        var transfer = _rules.TransferPatterns.FirstOrDefault(p => p.IsMatch(description));
        if (transfer is not null)
        {
            return CheckRef(draft with
            {
                RefNumber = line.CheckNo ?? AchRef,
                LineAccount = transfer.Account,
                Note = $"transfer: {transfer.Match}",
            });
        }

        if (!string.IsNullOrWhiteSpace(line.CheckNo))
        {
            return MapNumberedCheck(draft with { RefNumber = line.CheckNo.Trim() }, description);
        }

        return WithVendorAndTiers(draft with { RefNumber = AchRef }, description);
    }

    private MappedTxn MapDeposit(MappedTxn draft)
    {
        var (customer, note) = ResolvePayee(draft.Line, PartyRole.Customer, _payees.ResolveCustomer, "customer");
        if (customer.Name is null)
        {
            return Hold(draft, HoldReasons.UnknownPayee, customer.Candidates, customer.Note);
        }

        var withPayee = draft with { Payee = customer.Name, Note = note ?? draft.Note };
        return string.IsNullOrWhiteSpace(_rules.DepositIncomeAccount)
            ? Hold(withPayee, HoldReasons.NoDepositIncomeAccount) // SPEC-GAP T-002
            : withPayee with { LineAccount = _rules.DepositIncomeAccount };
    }

    /// <summary>Checks with a number carry no payee (requirement: "Blank if Check").</summary>
    private MappedTxn MapNumberedCheck(MappedTxn draft, string description)
    {
        var keyword = _tiers.KeywordRule(description);
        if (keyword is not null)
        {
            return CheckRef(draft with { LineAccount = keyword.Account, Confidence = keyword.Confidence, Tier = keyword.Tier });
        }

        return string.IsNullOrWhiteSpace(_rules.HoldingExpenseAccount)
            ? Hold(draft, HoldReasons.NoHoldingAccount) // SPEC-GAP T-002
            : CheckRef(draft with { LineAccount = _rules.HoldingExpenseAccount, Confidence = Confidence.Holding });
    }

    private MappedTxn WithVendorAndTiers(MappedTxn draft, string description)
    {
        var (vendor, note) = ResolvePayee(draft.Line, PartyRole.Vendor, _payees.ResolveVendor, "vendor");
        if (vendor.Name is null)
        {
            return Hold(draft, HoldReasons.UnknownPayee, vendor.Candidates, vendor.Note);
        }

        var withPayee = draft with { Payee = vendor.Name, Note = note ?? draft.Note };
        var tier = _tiers.Resolve(vendor.Name, description);
        return tier is null
            ? Hold(withPayee, HoldReasons.NoAccountRule) // tiers 3–4 are async: ModelTiers resolves these in the pipeline
            : CheckRef(withPayee with { LineAccount = tier.Account, Confidence = tier.Confidence, Tier = tier.Tier });
    }

    /// <summary>
    /// FR-6 payee from the description; when that finds none, FR-5 lets a matched invoice supply it.
    /// SPEC-GAP T-403: the invoice must have the role that fits the line (vendor for charges and ACH debits, customer for
    /// deposits) and its party must resolve to a known name by the same alias/fuzzy rules, so a name QuickBooks does not
    /// know is never posted. The returned note says where the payee came from.
    /// </summary>
    private (PayeeResolution Payee, string? Note) ResolvePayee(
        StatementLine line, PartyRole role, Func<string, PayeeResolution> resolve, string noun)
    {
        var fromLine = resolve(line.Description);
        if (fromLine.Name is not null
            || !_invoices.TryGetValue(line.RequestId, out var invoice)
            || invoice.Role != role)
        {
            return (fromLine, null);
        }

        var fromInvoice = resolve(invoice.Party);
        if (fromInvoice.Name is not null)
        {
            return (fromInvoice, $"payee from invoice {invoice.File}");
        }

        var note = $"invoice {invoice.File} names \"{invoice.Party}\", which is not a known {noun}";
        return (fromLine with { Note = fromLine.Note is null ? note : $"{fromLine.Note}; {note}" }, null);
    }

    /// <summary>qbXML RefNumber is at most 11 characters; a longer check number is held, never truncated (SPEC-GAP T-002).</summary>
    private static MappedTxn CheckRef(MappedTxn txn) =>
        txn.RefNumber is { Length: > MaxRefNumberLength } ? Hold(txn, HoldReasons.RefNumberTooLong) : txn;

    private static MappedTxn Hold(MappedTxn txn, string reason, IReadOnlyList<string>? candidates = null, string? note = null) =>
        txn with
        {
            Decision = Decision.Hold,
            Confidence = Confidence.Hold,
            Reason = reason,
            Candidates = candidates ?? txn.Candidates,
            Note = note ?? txn.Note,
        };

    private static string? Lookup(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
