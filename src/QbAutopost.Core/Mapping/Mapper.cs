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

    public Mapper(Rules rules, QbLists? lists = null, IReadOnlyList<LedgerEntry>? history = null)
    {
        _rules = rules;
        history ??= [];
        _payees = new PayeeResolver(rules, lists ?? QbLists.Empty, history);
        _tiers = new LineAccountTiers(rules, history);
    }

    public IReadOnlyList<MappedTxn> MapAll(IEnumerable<StatementLine> lines) => lines.Select(Map).ToList();

    public MappedTxn Map(StatementLine line) =>
        line.Kind == SourceKind.Card ? MapCard(line) : MapBank(line);

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
        var customer = _payees.ResolveCustomer(draft.Line.Description);
        if (customer.Name is null)
        {
            return Hold(draft, HoldReasons.UnknownPayee, customer.Candidates, customer.Note);
        }

        var withPayee = draft with { Payee = customer.Name };
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
        var vendor = _payees.ResolveVendor(draft.Line.Description);
        if (vendor.Name is null)
        {
            return Hold(draft, HoldReasons.UnknownPayee, vendor.Candidates, vendor.Note);
        }

        var withPayee = draft with { Payee = vendor.Name };
        var tier = _tiers.Resolve(vendor.Name, description);
        return tier is null
            ? Hold(withPayee, HoldReasons.NoAccountRule) // TODO(T-502): tiers 3–4 (invoice, Hermes T4)
            : CheckRef(withPayee with { LineAccount = tier.Account, Confidence = tier.Confidence, Tier = tier.Tier });
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
