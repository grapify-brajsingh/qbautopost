using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Tests.Text;

public sealed class FingerprintTests
{
    // Expected hash computed independently (PowerShell SHA256 over "bank|4521|2026-08-22|184.32|debit||HOME DEPOT #4521 NOIDA").
    private const string HomeDepotFingerprint = "9a5bfe3ab81d543e3a46fd4218414f8cee5608e588ce3c0d6968eb75e7d96e47";

    [Fact]
    public void Should_MatchKnownSha256_When_LineIsFixed()
    {
        var line = Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22");

        Assert.Equal(HomeDepotFingerprint, line.Fingerprint);
    }

    [Fact]
    public void Should_UseFirst16Chars_When_BuildingRequestId()
    {
        var line = Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22");

        Assert.Equal(HomeDepotFingerprint[..16], line.RequestId);
    }

    [Fact]
    public void Should_BeUnchanged_When_DescriptionDiffersOnlyInCaseAndSpacing()
    {
        var line = Lines.Bank(Direction.Debit, 184.32m, "  home   depot #4521 noida ", date: "2026-08-22");

        Assert.Equal(HomeDepotFingerprint, line.Fingerprint);
    }

    [Fact]
    public void Should_Change_When_AmountDiffers()
    {
        var line = Lines.Bank(Direction.Debit, 184.33m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22");

        Assert.NotEqual(HomeDepotFingerprint, line.Fingerprint);
    }

    [Fact]
    public void Should_Change_When_DirectionDiffers()
    {
        var line = Lines.Bank(Direction.Credit, 184.32m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22");

        Assert.NotEqual(HomeDepotFingerprint, line.Fingerprint);
    }

    [Fact]
    public void Should_IgnoreTrailingZeroScale_When_AmountIsEqual()
    {
        var a = Fingerprints.Compute(SourceKind.Bank, "4521", new DateOnly(2026, 8, 5), 420m, Direction.Debit, "1043", "CHECK 1043");
        var b = Fingerprints.Compute(SourceKind.Bank, "4521", new DateOnly(2026, 8, 5), 420.00m, Direction.Debit, "1043", "CHECK 1043");

        Assert.Equal(a, b);
    }
}
