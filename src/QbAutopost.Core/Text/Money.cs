using System.Globalization;

namespace QbAutopost.Core.Text;

public static class Money
{
    /// <summary>Invariant <c>0.00</c> — the only money format written anywhere.</summary>
    public static string Format(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a statement amount cell: optional <c>$</c>, thousands commas, leading/trailing minus or
    /// parentheses for negatives. Rejects more than two decimal places rather than rounding money.
    /// </summary>
    public static bool TryParse(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1].Trim();
        }
        else if (s.EndsWith('-'))
        {
            negative = true;
            s = s[..^1].Trim();
        }

        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || decimal.Round(value, 2) != value)
        {
            return false;
        }

        amount = negative ? -Math.Abs(value) : value;
        return true;
    }
}
