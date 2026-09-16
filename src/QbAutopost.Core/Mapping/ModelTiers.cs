using System.Globalization;
using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Mapping;

/// <summary>What <see cref="ModelTiers"/> needs from the job: G3 threshold, QuickBooks lists, ledger history, matched invoices.</summary>
public sealed record ModelTierInput(
    double Threshold,
    QbLists Lists,
    IReadOnlyList<LedgerEntry> History,
    IEnumerable<InvoiceFacts> Invoices,
    string? AuditDir);

/// <summary>
/// Spec FR-6 tiers 3–4 and gate G3 (FR-7), run after the synchronous <see cref="Mapper"/> on lines it held
/// <c>no-account-rule</c> (they already have a payee). One Hermes T4 call per line, in line order.
/// </summary>
public sealed class ModelTiers(AccountChooser chooser)
{
    public static bool Needs(MappedTxn txn) =>
        txn.Decision == Decision.Hold && txn.Reason == HoldReasons.NoAccountRule && txn.Payee is not null;

    public async Task<IReadOnlyList<MappedTxn>> ResolveAsync(IReadOnlyList<MappedTxn> mapped, ModelTierInput input, CancellationToken ct)
    {
        if (!mapped.Any(Needs))
        {
            return mapped;
        }

        var accounts = AccountChooser.ChoosableAccounts(input.Lists);
        var invoices = Mapper.InvoicesByLine(input.Invoices);
        var resolved = new List<MappedTxn>(mapped.Count);
        foreach (var txn in mapped)
        {
            resolved.Add(Needs(txn) ? await ResolveAsync(txn, accounts, invoices, input, ct) : txn);
        }

        return resolved;
    }

    private async Task<MappedTxn> ResolveAsync(
        MappedTxn txn,
        IReadOnlyList<string> accounts,
        Dictionary<string, InvoiceFacts> invoices,
        ModelTierInput input,
        CancellationToken ct)
    {
        var invoice = HintingInvoice(txn, invoices);
        var result = await chooser.ChooseAsync(
            new AccountQuestion(txn.Line, txn.Kind, txn.Payee!, invoice?.CategoryHint), accounts, input.AuditDir, ct);
        if (result.Choice is not { } choice)
        {
            return txn with { Reason = result.HoldReason, Note = Join(txn.Note, string.Join("; ", result.Errors)) };
        }

        var scored = txn with
        {
            Candidates = choice.Candidates,
            ModelConfidence = choice.Confidence,
            Note = Join(txn.Note, Describe(choice, invoice)),
        };

        if (invoice is not null && ConfidenceGate.InvoiceMayPost(choice.Confidence, input.Threshold))
        {
            return Post(scored, choice.Account, Confidence.Invoice, 3);
        }

        // SPEC-GAP T-502: a tier-3 answer below the threshold falls to tier 4 with the same answer (no second call
        // without the hint); G3 then holds it, since its score is already below the threshold.
        var hold = ConfidenceGate.CheckModel(choice.Confidence, txn.Payee!, choice.Account, input.Threshold, input.History);
        return hold is null
            ? Post(scored, choice.Account, Confidence.Model, 4)
            : scored with { Reason = hold, Tier = 4 };
    }

    /// <summary>
    /// SPEC-GAP T-502: tier 3 needs a matched invoice with a hint, from a vendor — lines reaching the tiers are vendor
    /// lines (card lines, ACH debits), and a customer invoice's hint says what was sold, not bought.
    /// </summary>
    private static InvoiceFacts? HintingInvoice(MappedTxn txn, Dictionary<string, InvoiceFacts> invoices) =>
        invoices.TryGetValue(txn.RequestId, out var invoice)
        && invoice.Role == PartyRole.Vendor
        && !string.IsNullOrWhiteSpace(invoice.CategoryHint)
            ? invoice
            : null;

    private static MappedTxn Post(MappedTxn txn, string account, Confidence confidence, int tier) => txn with
    {
        Decision = Decision.Post,
        Reason = null,
        Confidence = confidence,
        Tier = tier,
        LineAccount = account,
    };

    private static string Describe(AccountChoice choice, InvoiceFacts? invoice)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"model: {choice.Account} ({choice.Confidence:0.00})");
        if (invoice is not null)
        {
            text += $" with hint from invoice {invoice.File}";
        }

        return choice.Reason is null ? text : $"{text} — {choice.Reason}";
    }

    private static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second
        : string.IsNullOrEmpty(second) ? first
        : $"{first}; {second}";
}
