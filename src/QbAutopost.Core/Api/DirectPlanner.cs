using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.QbXml;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Api;

/// <summary>
/// FR-A-6: plans a direct request offline. The rows go through the same mapper (FR-6), the same in-batch and ledger
/// duplicate checks (G4) and the same qbXML builder (FR-9) as a folder job; only the SDK call is left out, so a
/// caller can validate before the QuickBooks server is even up.
/// <para>
/// The one thing this must never do is bless a row the post would hold. Everything the post decides without talking
/// to QuickBooks is decided here, from the same inputs, by the same code.
/// </para>
/// <para>
/// Tiers 3–4 (invoice hints and Hermes) do not run: they are asynchronous and this call is offline, so a row no rule
/// resolves is reported <c>no-account-rule</c> rather than guessed (Q-51). That errs towards holding, which is the
/// safe direction — a plan may be gloomier than the post, never rosier.
/// </para>
/// </summary>
public sealed class DirectPlanner(DirectLimits limits)
{
    public DirectPlan Plan(
        DirectRequest request,
        Rules rules,
        QbLists lists,
        Ledger ledger,
        DateOnly today,
        string qbXmlVersion = QbXmlBuilder.DefaultVersion)
    {
        var submitted = request.Transactions ?? [];
        var plan = new DirectPlan
        {
            Submitted = submitted.Count,
            SubmittedTotal = submitted.Sum(r => r.Amount),
            ControlTotal = request.ControlTotal,
        };

        var read = new DirectRequestReader(limits).Read(request, today);
        if (read.Errors.Count > 0)
        {
            // The request was refused whole (control total, limits, malformed rows): nothing is planned out of it.
            return plan with { Errors = read.Errors };
        }

        var mapped = MapLines(read.Lines, rules, lists, ledger);
        mapped = ApplyBatchGates(mapped, ledger);
        var unknown = new UnknownNames(lists);
        mapped = mapped.ToDictionary(pair => pair.Key, pair => unknown.Check(pair.Value));

        var rows = PlanRows(submitted, read, mapped);
        var toPost = rows
            .Where(r => r.Outcome == DirectOutcome.WouldPost)
            .Select(r => mapped[r.Index])
            .ToList();

        return plan with
        {
            Rows = rows,
            ToPost = toPost,
            QbXml = toPost.Count == 0 ? string.Empty : QbXmlBuilder.BuildAddRequest(toPost, qbXmlVersion),
            QbXmlCounts = CountByType(toPost),
            UnknownNames = unknown.Result,
            WouldPostTotal = toPost.Sum(t => t.Line.Amount),
        };
    }

    /// <summary>
    /// FR-6 mapping, one mapper per (statement kind, last four) group so the caller's own account name stands in for
    /// the <c>rules.json</c> lookup the folder path does. A caller names the account; they should not have to edit
    /// <c>rules.json</c> first. Two different accounts behind one last four is a contradiction, so both rows hold.
    /// </summary>
    private static Dictionary<int, MappedTxn> MapLines(
        IReadOnlyList<DirectLine> lines, Rules rules, QbLists lists, Ledger ledger)
    {
        var mapped = new Dictionary<int, MappedTxn>();
        foreach (var group in lines.GroupBy(l => (l.Line.Kind, l.Line.Last4), LastFour.Comparer))
        {
            var accounts = group.Select(l => l.Account).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var scoped = accounts.Count == 1
                ? WithAccount(rules, group.Key.Kind, group.Key.Last4, accounts[0])
                : rules;
            var mapper = new Mapper(scoped, lists, ledger.Posted);
            var tiers = new LineAccountTiers(scoped, ledger.Posted);
            var payees = new PayeeResolver(scoped, lists, ledger.Posted);

            foreach (var line in group)
            {
                var txn = mapper.Map(line.Line);
                mapped[line.Index] = accounts.Count == 1
                    ? WithCallerNames(txn, line, scoped, tiers, payees)
                    : Hold(
                        txn with { Account = line.Account },
                        HoldReasons.ConflictingLast4,
                        $"last four {group.Key.Last4} is sent as both {string.Join(" and ", accounts)}");
            }
        }

        return mapped;
    }

    /// <summary>The caller's account for this last four, so <see cref="Mapper"/> resolves it the way a rule would.</summary>
    private static Rules WithAccount(Rules rules, SourceKind kind, string last4, string account)
    {
        var map = (kind == SourceKind.Card ? rules.CardAccounts : rules.BankAccounts)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        map[last4] = account;
        return kind == SourceKind.Card ? rules with { CardAccounts = map } : rules with { BankAccounts = map };
    }

    /// <summary>
    /// The caller's <c>payee</c> and <c>lineAccount</c> (§6.1) override what the description resolved to: they are an
    /// explicit instruction, not a guess. A name the caller gave also clears the hold that asked for it — but only
    /// that hold, so a row held for any other reason stays held.
    /// </summary>
    private static MappedTxn WithCallerNames(
        MappedTxn txn, DirectLine line, Rules rules, LineAccountTiers tiers, PayeeResolver payees)
    {
        if (txn.Kind == TxnKind.Skip)
        {
            // A skip rule is a standing instruction about this kind of line; naming a payee does not revoke it.
            return txn;
        }

        if (line.Payee is { } payee)
        {
            var resolution = txn.Kind == TxnKind.Deposit ? payees.ResolveCustomer(payee) : payees.ResolveVendor(payee);
            // Aliases apply (§6.1); a name they do not know is used as sent — the lists check is what catches a typo.
            txn = txn with { Payee = resolution.Name ?? payee };
            if (txn.Reason == HoldReasons.UnknownPayee)
            {
                // The mapper stopped at the payee, so the line account it never reached is resolved here.
                txn = ResolveLineAccount(Release(txn), rules, tiers);
            }
        }

        if (line.LineAccount is not { } lineAccount)
        {
            return txn;
        }

        txn = txn with { LineAccount = lineAccount, Confidence = Confidence.Rule, Tier = 1 };
        return txn.Reason is HoldReasons.NoAccountRule or HoldReasons.NoHoldingAccount or HoldReasons.NoDepositIncomeAccount
            ? Release(txn)
            : txn;
    }

    /// <summary>The tail of FR-6 the mapper skips when it holds on the payee: tiers 1–2 for a charge, the income account for a deposit.</summary>
    private static MappedTxn ResolveLineAccount(MappedTxn txn, Rules rules, LineAccountTiers tiers)
    {
        if (txn.Kind == TxnKind.Deposit)
        {
            return string.IsNullOrWhiteSpace(rules.DepositIncomeAccount)
                ? Hold(txn, HoldReasons.NoDepositIncomeAccount)
                : txn with { LineAccount = rules.DepositIncomeAccount };
        }

        var tier = tiers.Resolve(txn.Payee!, TextNormalizer.Normalize(txn.Line.Description));
        return tier is null
            ? Hold(txn, HoldReasons.NoAccountRule)
            : txn with { LineAccount = tier.Account, Confidence = tier.Confidence, Tier = tier.Tier };
    }

    /// <summary>
    /// The batch-level half of G4, the same two checks <see cref="Pipeline.JobPipeline"/> makes on a folder job:
    /// identical rows inside one request hold each other, and a fingerprint the ledger already has is a duplicate.
    /// </summary>
    private static Dictionary<int, MappedTxn> ApplyBatchGates(Dictionary<int, MappedTxn> mapped, Ledger ledger)
    {
        var counts = mapped.Values
            .GroupBy(m => m.Line.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return mapped.ToDictionary(pair => pair.Key, pair =>
        {
            var txn = pair.Value;
            if (txn.Decision == Decision.Skip)
            {
                return txn;
            }

            if (counts[txn.Line.Fingerprint] > 1)
            {
                // Identical rows share one request id, so posting both would post one transaction twice under it.
                return Hold(txn, HoldReasons.DuplicateLine, "identical row appears more than once in this request");
            }

            var posted = ledger.Posted.FirstOrDefault(p =>
                !p.Undone && string.Equals(p.Fingerprint, txn.Line.Fingerprint, StringComparison.Ordinal));
            return posted is null
                ? txn
                : txn with { Decision = Decision.Skip, Reason = HoldReasons.AlreadyPosted, Note = posted.BatchId };
        });
    }

    private static IReadOnlyList<DirectPlanRow> PlanRows(
        IReadOnlyList<DirectRow> submitted, DirectReadResult read, IReadOnlyDictionary<int, MappedTxn> mapped)
    {
        var held = read.Held.ToDictionary(h => h.Index);
        var rows = new List<DirectPlanRow>(submitted.Count);
        for (var index = 0; index < submitted.Count; index++)
        {
            rows.Add(held.TryGetValue(index, out var issue)
                ? ReaderHeldRow(index, submitted[index], issue)
                : MappedRow(index, submitted[index], mapped[index]));
        }

        return rows;
    }

    /// <summary>A row the reader would not turn into a line at all (over the cap, outside the window, no account).</summary>
    private static DirectPlanRow ReaderHeldRow(int index, DirectRow row, DirectRowIssue issue) => new()
    {
        Index = index,
        ExternalId = issue.ExternalId,
        Outcome = DirectOutcome.Held,
        Kind = row.Kind,
        Account = row.Account,
        Amount = row.Amount,
        Reason = issue.Reason,
    };

    private static DirectPlanRow MappedRow(int index, DirectRow row, MappedTxn txn) => new()
    {
        Index = index,
        ExternalId = row.ExternalId,
        RequestId = txn.RequestId,
        Outcome = Outcome(txn),
        Kind = txn.Kind,
        Account = txn.Account,
        LineAccount = txn.LineAccount,
        Payee = txn.Payee,
        Amount = txn.Line.Amount,
        Confidence = txn.Confidence,
        Reason = txn.Reason,
        Note = txn.Reason == HoldReasons.AlreadyPosted ? null : txn.Note,
        Candidates = txn.Candidates,
        BatchId = txn.Reason == HoldReasons.AlreadyPosted ? txn.Note : null,
    };

    private static DirectOutcome Outcome(MappedTxn txn) => txn.Decision switch
    {
        Decision.Post => DirectOutcome.WouldPost,
        Decision.Hold => DirectOutcome.Held,
        _ => txn.Reason == HoldReasons.AlreadyPosted ? DirectOutcome.Duplicate : DirectOutcome.Skipped,
    };

    private static IReadOnlyDictionary<string, int> CountByType(IReadOnlyList<MappedTxn> toPost) =>
        toPost
            .GroupBy(
                t => t.Kind switch
                {
                    TxnKind.Check => "CheckAdd",
                    TxnKind.CcCharge => "CreditCardChargeAdd",
                    TxnKind.CcCredit => "CreditCardCreditAdd",
                    _ => "DepositAdd",
                },
                StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static MappedTxn Hold(MappedTxn txn, string reason, string? note = null) => txn with
    {
        Decision = Decision.Hold,
        Confidence = Confidence.Hold,
        Reason = reason,
        Note = note ?? txn.Note,
    };

    /// <summary>Undoes a hold the caller has answered, leaving the rest of the mapping as it was.</summary>
    private static MappedTxn Release(MappedTxn txn) => txn with
    {
        Decision = Decision.Post,
        Confidence = Confidence.Rule,
        Reason = null,
        Candidates = [],
    };

    private static class LastFour
    {
        public static IEqualityComparer<(SourceKind Kind, string Last4)> Comparer { get; } = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<(SourceKind Kind, string Last4)>
        {
            public bool Equals((SourceKind Kind, string Last4) x, (SourceKind Kind, string Last4) y) =>
                x.Kind == y.Kind && string.Equals(x.Last4, y.Last4, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((SourceKind Kind, string Last4) obj) =>
                HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Last4));
        }
    }

    /// <summary>
    /// FR-A-6 <c>unknownNames</c>: every name the request would send that <c>qb-lists.json</c> does not have, and a
    /// hold on the row that would send it — QuickBooks would reject it anyway, and a rejection after the fact is
    /// harder to read than a hold. Each list is consulted only when it has entries, so a missing sync is a warning
    /// (T-902) and not a refusal. Comparison is exact and ordinal (Q-35): QuickBooks names differ by punctuation.
    /// </summary>
    private sealed class UnknownNames(QbLists lists)
    {
        private readonly HashSet<string> _accounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _vendors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _customers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _known = new(lists.Accounts.Select(a => a.Name), StringComparer.Ordinal);

        public DirectUnknownNames Result => new(
            [.. _accounts.Order(StringComparer.Ordinal)],
            [.. _vendors.Order(StringComparer.Ordinal)],
            [.. _customers.Order(StringComparer.Ordinal)]);

        public MappedTxn Check(MappedTxn txn)
        {
            if (txn.Decision != Decision.Post)
            {
                return txn;
            }

            var missing = new[] { txn.Account, txn.LineAccount }
                .Where(name => !string.IsNullOrWhiteSpace(name) && _known.Count > 0 && !_known.Contains(name))
                .Select(name => name!)
                .ToList();
            if (missing.Count > 0)
            {
                foreach (var name in missing)
                {
                    _accounts.Add(name);
                }

                return Hold(txn, HoldReasons.UnknownAccount, "not in qb-lists.json: " + string.Join(", ", missing));
            }

            var deposit = txn.Kind == TxnKind.Deposit;
            var names = deposit ? lists.Customers : lists.Vendors;
            if (txn.Payee is not { } payee || names.Count == 0 || names.Contains(payee, StringComparer.Ordinal))
            {
                return txn;
            }

            _ = (deposit ? _customers : _vendors).Add(payee);
            return Hold(txn, HoldReasons.UnknownPayee, $"not in qb-lists.json: {payee}");
        }
    }
}
