using System.Text.RegularExpressions;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Extract;

/// <summary>
/// Deterministic requirement parser (spec FR-2 fallback; M1 stand-in for Hermes T1).
/// SPEC-GAP T-002: written from the template in spec §1. Real last-four values are expected as 4-digit groups
/// on the account lines: "Bank Account=… 4521", "Credit Card Charges &amp; Credits= … 7788", "Account From = Bank 4521".
/// </summary>
public static partial class RegexSpecParser
{
    public const string CompanyPlaceholder = "Company";

    public static JobSpec Parse(string requirement)
    {
        string? company = null;
        string? section = null;
        var kinds = new SortedSet<TxnKind>();
        var bank = new List<string>();
        var card = new List<string>();
        var fieldRules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in requirement.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (section is null && company is null && MenuLine().Match(line) is { Success: true } menu)
            {
                company = menu.Groups["company"].Value.Trim();
                continue;
            }

            if (TypeLine().Match(line) is { Success: true } type)
            {
                section = Classify(type.Groups["type"].Value, kinds);
                continue;
            }

            if (section is null || KeyValue().Match(line) is not { Success: true } kv)
            {
                continue;
            }

            var key = kv.Groups["key"].Value.Trim();
            var value = kv.Groups["value"].Value.Trim();
            if (!fieldRules.TryGetValue(section, out var rules))
            {
                rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                fieldRules[section] = rules;
            }

            rules[key] = value;
            var target = AccountList(section, key, bank, card);
            if (target is not null)
            {
                foreach (Match m in FourDigits().Matches(value))
                {
                    if (!target.Contains(m.Value))
                    {
                        target.Add(m.Value);
                    }
                }
            }
        }

        return new JobSpec
        {
            Company = string.Equals(company, CompanyPlaceholder, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(company)
                ? null
                : company,
            Kinds = kinds.ToList(),
            BankLast4 = bank,
            CardLast4 = card,
            FieldRules = fieldRules.ToDictionary(
                p => p.Key,
                p => (IReadOnlyDictionary<string, string>)p.Value,
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string? Classify(string type, SortedSet<TxnKind> kinds)
    {
        if (type.Contains("credit card", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(TxnKind.CcCharge);
            kinds.Add(TxnKind.CcCredit);
            return "card";
        }

        if (type.Contains("check", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(TxnKind.Check);
            return "check";
        }

        if (type.Contains("deposit", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(TxnKind.Deposit);
            return "deposit";
        }

        return null; // unknown type: ignored; G2 fails the job if no kind resolves
    }

    private static List<string>? AccountList(string section, string key, List<string> bank, List<string> card) => section switch
    {
        "check" when key.StartsWith("Bank Account", StringComparison.OrdinalIgnoreCase) => bank,
        "deposit" when key.StartsWith("Account From", StringComparison.OrdinalIgnoreCase)
                       || key.StartsWith("Deposit To", StringComparison.OrdinalIgnoreCase) => bank,
        "card" when key.StartsWith("Credit Card", StringComparison.OrdinalIgnoreCase) => card,
        _ => null,
    };

    [GeneratedRegex(@"^(?<company>[^>]+?)\s*\.?\s*>\s*Batch\s+Enter\s+Transactions", RegexOptions.IgnoreCase)]
    private static partial Regex MenuLine();

    [GeneratedRegex(@"^Transactions?\s+Type\s*=?\s*>\s*(?<type>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeLine();

    [GeneratedRegex(@"^(?<key>[^=]+?)\s*(?:=|-(?=\s|$))\s*(?<value>.*)$")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)")]
    private static partial Regex FourDigits();
}
