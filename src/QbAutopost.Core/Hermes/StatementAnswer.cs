using System.Globalization;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// Hermes T2 answer for one part of a statement (spec §9.2). Validation: known kind and direction, ISO dates,
/// amounts &gt; 0 with at most 2 decimals, last-four of exactly 4 digits. G1 (FR-4) checks the figures afterwards.
/// </summary>
public sealed record StatementAnswer : IValidatable
{
    public const string IsoDate = "yyyy-MM-dd";

    public string? AccountLast4 { get; init; }
    public string? Kind { get; init; }
    public string? PeriodStart { get; init; }
    public string? PeriodEnd { get; init; }
    public decimal? OpeningBalance { get; init; }
    public decimal? ClosingBalance { get; init; }
    public int? TransactionCount { get; init; }
    public List<StatementAnswerRow?>? Rows { get; init; }

    public SourceKind SourceKind => ParseKind(Kind) ?? throw new InvalidOperationException("Answer was not validated.");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ParseKind(Kind) is null)
        {
            errors.Add($"kind is \"{Kind}\"; allowed values are \"bank\" and \"card\".");
        }

        if (AccountLast4 is not null && (AccountLast4.Length != 4 || !AccountLast4.All(char.IsAsciiDigit)))
        {
            errors.Add($"accountLast4 is \"{AccountLast4}\"; it must be exactly 4 digits, or null when not shown.");
        }

        var start = CheckOptionalDate("periodStart", PeriodStart, errors);
        var end = CheckOptionalDate("periodEnd", PeriodEnd, errors);
        if (start > end)
        {
            errors.Add($"periodStart {PeriodStart} is after periodEnd {PeriodEnd}.");
        }

        CheckMoney("openingBalance", OpeningBalance, errors);
        CheckMoney("closingBalance", ClosingBalance, errors);
        if (TransactionCount < 0)
        {
            errors.Add($"transactionCount is {TransactionCount}; it must be 0 or more, or null when not printed.");
        }

        if (Rows is null)
        {
            errors.Add("rows is required (use [] when this part has no transactions).");
            return errors;
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            ValidateRow($"rows[{i}]", Rows[i], errors);
        }

        return errors;
    }

    /// <summary>The validated date text as a <see cref="DateOnly"/> (null stays null).</summary>
    public static DateOnly? ToDate(string? text) =>
        text is null ? null : DateOnly.ParseExact(text, IsoDate, CultureInfo.InvariantCulture);

    public static Direction ParseDirection(string text) =>
        Enum.Parse<Direction>(text, ignoreCase: true);

    private static void ValidateRow(string name, StatementAnswerRow? row, List<string> errors)
    {
        if (row is null)
        {
            errors.Add($"{name} is null; every row must be an object.");
            return;
        }

        if (row.Date is null || !TryDate(row.Date, out _))
        {
            errors.Add($"{name}.date is \"{row.Date}\"; it must be a real date written yyyy-MM-dd.");
        }

        if (string.IsNullOrWhiteSpace(row.Description))
        {
            errors.Add($"{name}.description is empty.");
        }

        if (!string.Equals(row.Direction, "debit", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(row.Direction, "credit", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{name}.direction is \"{row.Direction}\"; allowed values are \"debit\" and \"credit\".");
        }

        if (row.Amount is not { } amount)
        {
            errors.Add($"{name}.amount is missing.");
        }
        else if (amount <= 0m)
        {
            errors.Add($"{name}.amount is {amount}; it must be greater than 0 (direction carries the sign).");
        }
        else
        {
            CheckMoney($"{name}.amount", amount, errors);
        }

        CheckMoney($"{name}.balance", row.Balance, errors);
        if (row.CheckNo is not null && string.IsNullOrWhiteSpace(row.CheckNo))
        {
            errors.Add($"{name}.checkNo is blank; use null when the line is not a check.");
        }
    }

    private static SourceKind? ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "bank" => SourceKind.Bank,
        "card" => SourceKind.Card,
        _ => null,
    };

    private static DateOnly? CheckOptionalDate(string name, string? text, List<string> errors)
    {
        if (text is null)
        {
            return null;
        }

        if (TryDate(text, out var date))
        {
            return date;
        }

        errors.Add($"{name} is \"{text}\"; it must be a real date written yyyy-MM-dd, or null when not shown.");
        return null;
    }

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, IsoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static void CheckMoney(string name, decimal? value, List<string> errors)
    {
        if (value is { } v && decimal.Round(v, 2) != v)
        {
            errors.Add($"{name} is {v}; money has at most 2 decimal places.");
        }
    }
}

/// <summary>One transaction row of a T2 answer (spec §9.2).</summary>
public sealed record StatementAnswerRow
{
    public string? Date { get; init; }
    public string? Description { get; init; }
    public string? Direction { get; init; }
    public decimal? Amount { get; init; }
    public string? CheckNo { get; init; }
    public decimal? Balance { get; init; }
}
