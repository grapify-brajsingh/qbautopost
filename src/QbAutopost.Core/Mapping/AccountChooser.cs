using System.Globalization;
using System.Text;
using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Models;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

/// <summary>What Hermes T4 is asked about one line: the line, its FR-6 kind, the resolved payee and the invoice hint.</summary>
public sealed record AccountQuestion(StatementLine Line, TxnKind Kind, string Payee, string? InvoiceHint);

/// <summary>
/// A validated T4 answer. <see cref="Candidates"/> = the account, then the listed alternatives, distinct, at most
/// <see cref="AccountChooser.MaxCandidates"/> (FR-7: held lines carry the top-3 accounts).
/// </summary>
public sealed record AccountChoice(string Account, double Confidence, string? Reason, IReadOnlyList<string> Candidates);

/// <summary>What <see cref="AccountChooser"/> returned for one line: a choice, or a hold reason with errors.</summary>
public sealed record AccountChoiceResult
{
    public AccountChoice? Choice { get; init; }
    public string? HoldReason { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsHeld => HoldReason is not null;
}

/// <summary>
/// Hermes T4 (spec FR-6 tiers 3–4, §9.4): asks for a line account from a fixed list. The answer must name a listed
/// account exactly; anything else, or a Hermes failure, holds the line. The confidence threshold and G3 are applied by
/// the caller.
/// </summary>
public sealed class AccountChooser(IHermesClient hermes, PromptLibrary prompts)
{
    public const int MaxCandidates = 3;

    /// <summary>§9.4 "expense/income/COGS/other-expense" as QuickBooks AccountType values.</summary>
    private static readonly HashSet<string> ChoosableTypes =
        new(["Expense", "Income", "CostOfGoodsSold", "OtherExpense"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    /// <summary>
    /// The accounts T4 may choose from, in list order, blank and repeated names removed. When any account has a type,
    /// only the §9.4 types are kept; otherwise every account is.
    /// SPEC-GAP T-501: an untyped account in a list that has types is dropped, and OtherIncome is not a §9.4 type.
    /// </summary>
    public static IReadOnlyList<string> ChoosableAccounts(QbLists lists)
    {
        var typesKnown = lists.Accounts.Any(a => !string.IsNullOrWhiteSpace(a.Type));
        return lists.Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Where(a => !typesKnown || (a.Type is not null && ChoosableTypes.Contains(a.Type)))
            .Select(a => a.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<AccountChoiceResult> ChooseAsync(
        AccountQuestion question, IReadOnlyList<string> accounts, string? auditDir, CancellationToken ct)
    {
        if (accounts.Count == 0)
        {
            // SPEC-GAP T-501: no account list (qb-lists.json not synced) → nothing to choose from; the line is held.
            return Held(HoldReasons.NoAccounts, "no QuickBooks accounts to choose from; run POST /qb/sync-lists");
        }

        var request = new HermesRequest(
            HermesTask.Account,
            prompts.Render(HermesTask.Account, NoValues),
            UserContent(question, accounts),
            auditDir,
            Check: AccountAnswer.MustBeOneOf(accounts));
        try
        {
            var answer = await hermes.CompleteJsonAsync<AccountAnswer>(request, ct);
            return new AccountChoiceResult { Choice = ToChoice(answer, accounts) };
        }
        catch (HermesException ex)
        {
            return Held(HoldReasons.HermesFailed, ex.Message);
        }
    }

    private static string UserContent(AccountQuestion question, IReadOnlyList<string> accounts)
    {
        var line = question.Line;
        var hint = string.IsNullOrWhiteSpace(question.InvoiceHint) ? "none" : question.InvoiceHint.Trim();
        return new StringBuilder()
            .Append("Transaction:\n")
            .Append("date: ").Append(line.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n')
            .Append("description: ").Append(line.Description).Append('\n')
            .Append("amount: ").Append(Money.Format(line.Amount)).Append('\n')
            .Append("direction: ").Append(Name(line.Direction)).Append('\n')
            .Append("kind: ").Append(Name(question.Kind)).Append('\n')
            .Append("payee: ").Append(question.Payee).Append('\n')
            .Append("invoice hint: ").Append(hint).Append('\n')
            .Append("\nAccounts (one per line):\n")
            .AppendJoin('\n', accounts)
            .ToString();
    }

    /// <summary>SPEC-GAP T-501: candidates keep only listed names, so a held line never suggests an unknown account.</summary>
    private static AccountChoice ToChoice(AccountAnswer answer, IReadOnlyList<string> accounts)
    {
        var allowed = accounts.ToHashSet(StringComparer.Ordinal);
        var candidates = (answer.Alternatives ?? [])
            .Prepend(answer.Account)
            .OfType<string>()
            .Where(allowed.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();
        var reason = string.IsNullOrWhiteSpace(answer.Reason) ? null : answer.Reason.Trim();
        return new AccountChoice(answer.Account!, answer.Confidence!.Value, reason, candidates);
    }

    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static AccountChoiceResult Held(string reason, string error) => new() { HoldReason = reason, Errors = [error] };
}
