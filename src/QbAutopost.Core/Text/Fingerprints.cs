using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Text;

/// <summary>
/// <c>sha256(kind|last4|date|amount|direction|checkNo|Normalize(description))</c>, lower-hex (spec §7).
/// SPEC-GAP T-002: kind/direction are written lower-case (<c>bank</c>, <c>debit</c>), a missing check number as "".
/// </summary>
public static class Fingerprints
{
    public const int RequestIdLength = 16;

    public static string Compute(StatementLine line) =>
        Compute(line.Kind, line.Last4, line.Date, line.Amount, line.Direction, line.CheckNo, line.Description);

    public static string Compute(
        SourceKind kind, string last4, DateOnly date, decimal amount, Direction direction, string? checkNo, string description)
    {
        var raw = string.Join(
            '|',
            kind.ToString().ToLowerInvariant(),
            last4,
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Money.Format(amount),
            direction.ToString().ToLowerInvariant(),
            checkNo?.Trim() ?? "",
            TextNormalizer.Normalize(description));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static string ToRequestId(string fingerprint) => fingerprint[..RequestIdLength];
}
