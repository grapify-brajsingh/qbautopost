using System.Text.RegularExpressions;

namespace QbAutopost.Core.Extract;

/// <summary>Account last-four detection (spec F5).</summary>
public static partial class Last4Detector
{
    /// <summary>
    /// The one distinct <c>(?&lt;!\d)(\d{4})(?!\d)</c> group in the file name (extension excluded).
    /// SPEC-GAP T-002: several distinct groups (e.g. a year and an account) → null, so the caller falls back
    /// to the statement content or holds the statement, instead of guessing which group is the account.
    /// </summary>
    public static string? FromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var hits = FourDigits().Matches(stem).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Last four digits of a cell such as <c>XXXX4521</c> or <c>...4521</c>; null when it has fewer than four digits.</summary>
    public static string? FromCell(string? cell)
    {
        var digits = new string((cell ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : null;
    }

    [GeneratedRegex(@"(?<!\d)(\d{4})(?!\d)")]
    private static partial Regex FourDigits();
}
