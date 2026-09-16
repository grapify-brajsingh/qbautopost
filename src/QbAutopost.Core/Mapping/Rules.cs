using System.Text.Json;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Mapping;

/// <summary>
/// <c>rules.json</c> (spec §11). SPEC-GAP T-002: the POC base schema was not available; the keys below are
/// the ones spec FR-6/FR-14 reference, with defaults chosen here (see tracker Questions).
/// </summary>
public sealed record Rules
{
    /// <summary>Bank statement last-four → QuickBooks bank account name.</summary>
    public IReadOnlyDictionary<string, string> BankAccounts { get; init; } = Empty;

    /// <summary>Card statement last-four → QuickBooks credit-card account name.</summary>
    public IReadOnlyDictionary<string, string> CardAccounts { get; init; } = Empty;

    /// <summary>Description fragment → vendor name.</summary>
    public IReadOnlyDictionary<string, string> PayeeAliases { get; init; } = Empty;

    /// <summary>Description fragment → customer name.</summary>
    public IReadOnlyDictionary<string, string> CustomerAliases { get; init; } = Empty;

    /// <summary>Vendor name → expense account (tier 1).</summary>
    public IReadOnlyDictionary<string, string> VendorAccounts { get; init; } = Empty;

    /// <summary>Description keyword → account (tier 1), first match wins.</summary>
    public IReadOnlyList<PatternRule> KeywordAccounts { get; init; } = [];

    /// <summary>Bank debits that are transfers (e.g. card payments) → liability/bank account.</summary>
    public IReadOnlyList<PatternRule> TransferPatterns { get; init; } = [];

    /// <summary>Card lines never posted (e.g. the card payment already booked from the bank side).</summary>
    public IReadOnlyList<string> SkipPatterns { get; init; } = [];

    public IReadOnlyDictionary<string, CsvLayout> CsvLayouts { get; init; } = new Dictionary<string, CsvLayout>();

    /// <summary>Line account for numbered checks with no keyword rule (Confidence <c>Holding</c>). Null → such checks are held.</summary>
    public string? HoldingExpenseAccount { get; init; }

    /// <summary>Line account for deposits (Q2 assumed). Null → deposits are held.</summary>
    public string? DepositIncomeAccount { get; init; }

    public double FuzzyThreshold { get; init; } = 0.85;
    public int HistoryMinCount { get; init; } = 3;
    public int InvoiceMatchDays { get; init; } = 5;
    public double ModelConfidenceThreshold { get; init; } = 0.8;

    private static IReadOnlyDictionary<string, string> Empty { get; } = new Dictionary<string, string>();

    public static Rules Load(string path) => Parse(File.ReadAllText(path));

    public static Rules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<Rules>(json, JsonOptions.Default)
            ?? throw new InvalidDataException("rules.json is empty.");

        // SPEC-GAP T-502: G3 depends on this value; 0 or less would let any model answer through, over 1 nothing.
        if (rules.ModelConfidenceThreshold is not (> 0 and <= 1))
        {
            throw new InvalidDataException(
                $"rules.json ModelConfidenceThreshold is {rules.ModelConfidenceThreshold}; it must be greater than 0 and at most 1.");
        }

        return rules.Normalized();
    }

    /// <summary>Name/last-four lookups are case-insensitive; JSON nulls become empty collections.</summary>
    private Rules Normalized() => this with
    {
        BankAccounts = IgnoreCase(BankAccounts),
        CardAccounts = IgnoreCase(CardAccounts),
        PayeeAliases = IgnoreCase(PayeeAliases),
        CustomerAliases = IgnoreCase(CustomerAliases),
        VendorAccounts = IgnoreCase(VendorAccounts),
        KeywordAccounts = KeywordAccounts ?? [],
        TransferPatterns = TransferPatterns ?? [],
        SkipPatterns = SkipPatterns ?? [],
        CsvLayouts = new Dictionary<string, CsvLayout>(
            CsvLayouts ?? new Dictionary<string, CsvLayout>(), StringComparer.OrdinalIgnoreCase),
    };

    private static Dictionary<string, string> IgnoreCase(IReadOnlyDictionary<string, string>? source) =>
        new(source ?? Empty, StringComparer.OrdinalIgnoreCase);
}
