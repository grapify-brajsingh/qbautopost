using QbAutopost.Core.Models;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Builders for statement lines used by mapping, gate and qbXML tests.</summary>
internal static class Lines
{
    public static StatementLine Bank(
        Direction direction, decimal amount, string description, string? checkNo = null,
        decimal? balance = null, string date = "2026-08-12", string last4 = "4521", int lineNo = 1) => new()
    {
        SourceFile = "chase-checking-4521.csv",
        Kind = SourceKind.Bank,
        Last4 = last4,
        LineNo = lineNo,
        Date = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
        Description = description,
        Direction = direction,
        Amount = amount,
        CheckNo = checkNo,
        Balance = balance,
    };

    public static StatementLine Card(
        Direction direction, decimal amount, string description, string date = "2026-08-03", string last4 = "7788", int lineNo = 1) => new()
    {
        SourceFile = "chase-card-7788.csv",
        Kind = SourceKind.Card,
        Last4 = last4,
        LineNo = lineNo,
        Date = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
        Description = description,
        Direction = direction,
        Amount = amount,
    };
}
