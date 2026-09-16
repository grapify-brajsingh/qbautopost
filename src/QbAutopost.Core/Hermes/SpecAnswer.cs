using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Hermes;

/// <summary>Hermes T1 answer (spec §9.1). Validation: kinds ⊆ known; every last-four is exactly 4 digits.</summary>
public sealed record SpecAnswer : IValidatable
{
    /// <summary>T1 kind names → pipeline kinds; <c>CreditCard</c> covers charges and credits (FR-2 AC).</summary>
    public static readonly IReadOnlyDictionary<string, TxnKind[]> KnownKinds =
        new Dictionary<string, TxnKind[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Check"] = [TxnKind.Check],
            ["CreditCard"] = [TxnKind.CcCharge, TxnKind.CcCredit],
            ["Deposit"] = [TxnKind.Deposit],
        };

    public string? Company { get; init; }

    public List<string>? Kinds { get; init; }

    public List<string>? BankLast4 { get; init; }

    public List<string>? CardLast4 { get; init; }

    public Dictionary<string, Dictionary<string, string>>? FieldRules { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Kinds is null)
        {
            errors.Add("kinds is required (use [] when no section is recognised).");
        }
        else
        {
            errors.AddRange(Kinds
                .Where(k => k is null || !KnownKinds.ContainsKey(k))
                .Select(k => $"kinds contains \"{k}\"; allowed values are {string.Join(", ", KnownKinds.Keys)}."));
        }

        CheckLast4(nameof(BankLast4), BankLast4, errors);
        CheckLast4(nameof(CardLast4), CardLast4, errors);
        return errors;
    }

    /// <summary>The validated answer as a <see cref="JobSpec"/>: kinds expanded and ordered, duplicates removed, placeholder company dropped.</summary>
    public JobSpec ToJobSpec()
    {
        var company = Company?.Trim();
        return new JobSpec
        {
            Company = string.IsNullOrEmpty(company)
                      || string.Equals(company, RegexSpecParser.CompanyPlaceholder, StringComparison.OrdinalIgnoreCase)
                ? null
                : company,
            Kinds = (Kinds ?? []).SelectMany(k => KnownKinds[k]).Distinct().Order().ToList(),
            BankLast4 = (BankLast4 ?? []).Distinct(StringComparer.Ordinal).ToList(),
            CardLast4 = (CardLast4 ?? []).Distinct(StringComparer.Ordinal).ToList(),
            FieldRules = (FieldRules ?? []).ToDictionary(
                section => section.Key,
                section => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    section.Value ?? [], StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private static void CheckLast4(string field, List<string>? values, List<string> errors)
    {
        var name = char.ToLowerInvariant(field[0]) + field[1..];
        if (values is null)
        {
            errors.Add($"{name} is required (use [] when none is stated).");
            return;
        }

        errors.AddRange(values
            .Where(v => v is not { Length: 4 } || !v.All(char.IsAsciiDigit))
            .Select(v => $"{name} contains \"{v}\"; each last-four must be exactly 4 digits."));
    }
}
