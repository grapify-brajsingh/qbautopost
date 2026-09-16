namespace QbAutopost.Core.Output;

/// <summary>RFC 4180 cell/row formatting for the paste-ready sheets.</summary>
public static class CsvText
{
    private static readonly char[] FormulaStarts = ['=', '+', '-', '@', '\t', '\r'];
    private static readonly char[] NeedsQuoting = [',', '"', '\r', '\n'];

    /// <summary>
    /// A free-text cell. A leading formula character is prefixed with <c>'</c> so Excel shows it as text
    /// (CSV injection guard); Excel hides the apostrophe when the cell is copied into QuickBooks.
    /// </summary>
    public static string Text(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && Array.IndexOf(FormulaStarts, s[0]) >= 0)
        {
            s = "'" + s;
        }

        return Quote(s);
    }

    /// <summary>A cell produced by this app (dates, money) — never formula-escaped.</summary>
    public static string Raw(string value) => Quote(value);

    public static string Row(params string[] cells) => string.Join(',', cells);

    private static string Quote(string s) =>
        s.IndexOfAny(NeedsQuoting) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
}
