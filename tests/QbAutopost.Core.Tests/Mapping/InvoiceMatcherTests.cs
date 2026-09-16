using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

/// <summary>
/// FR-5 matching: amount within 0.005 and date within <c>InvoiceMatchDays</c>; one candidate matches; several → same
/// direction, then best payee similarity ≥ <c>FuzzyThreshold</c>; ties → no match (<c>ambiguous</c>).
/// </summary>
public sealed class InvoiceMatcherTests
{
    private static readonly Rules Rules = new() { InvoiceMatchDays = 5, FuzzyThreshold = 0.85 };

    private static readonly StatementLine HomeDepot =
        Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22", lineNo: 2);

    [Fact]
    public void Should_MatchTheLine_When_ItIsTheOnlyCandidate()
    {
        var match = MatchOne(Invoice(), HomeDepot, Lines.Bank(Direction.Debit, 311.40m, "FPL", date: "2026-08-21"));

        Assert.Equal(HomeDepot.RequestId, match.Invoice.MatchedRequestId);
        Assert.True(match.Matched);
        Assert.Null(match.Reason);
        Assert.Equal([HomeDepot.RequestId], match.Candidates);
    }

    [Fact]
    public void Should_MatchTheLine_When_OnlyCandidateHasOtherDirectionAndUnrelatedText()
    {
        // FR-5: "One candidate → match"; direction and payee only break ties.
        var refund = Lines.Bank(Direction.Credit, 184.32m, "ONLINE REFUND 99", date: "2026-08-22");

        var match = MatchOne(Invoice(), refund);

        Assert.Equal(refund.RequestId, match.Invoice.MatchedRequestId);
    }

    [Theory]
    [InlineData(184.325)]
    [InlineData(184.315)]
    public void Should_MatchTheLine_When_AmountDiffersByAtMostHalfACent(double total)
    {
        var match = MatchOne(Invoice(total: (decimal)total), HomeDepot);

        Assert.True(match.Matched);
    }

    [Theory]
    [InlineData(184.33)]
    [InlineData(184.31)]
    [InlineData(1843.20)]
    public void Should_NotMatch_When_AmountDiffersByMoreThanHalfACent(double total)
    {
        var match = MatchOne(Invoice(total: (decimal)total), HomeDepot);

        Assert.False(match.Matched);
        Assert.Equal(HoldReasons.NoMatchingLine, match.Reason);
        Assert.Empty(match.Candidates);
    }

    [Theory]
    [InlineData("2026-08-17")]
    [InlineData("2026-08-27")]
    public void Should_MatchTheLine_When_DatesAreExactlyTheWindowApart(string date)
    {
        var match = MatchOne(Invoice(date: date), HomeDepot);

        Assert.True(match.Matched);
    }

    [Theory]
    [InlineData("2026-08-16")]
    [InlineData("2026-08-28")]
    public void Should_NotMatch_When_DatesAreFurtherApartThanTheWindow(string date)
    {
        var match = MatchOne(Invoice(date: date), HomeDepot);

        Assert.Equal(HoldReasons.NoMatchingLine, match.Reason);
    }

    [Fact]
    public void Should_UseConfiguredWindow_When_RulesChangeInvoiceMatchDays()
    {
        var matches = InvoiceMatcher.Match([Invoice(date: "2026-08-12")], [HomeDepot], Rules with { InvoiceMatchDays = 10 });

        Assert.True(Assert.Single(matches).Matched);
    }

    [Fact]
    public void Should_NotMatch_When_InvoiceHasNoDate()
    {
        var match = MatchOne(Invoice() with { Date = null }, HomeDepot);

        Assert.False(match.Matched);
        Assert.Equal(HoldReasons.NoInvoiceDate, match.Reason);
    }

    [Fact]
    public void Should_PreferDebit_When_VendorInvoiceHasCandidatesInBothDirections()
    {
        var credit = Lines.Bank(Direction.Credit, 184.32m, "HOME DEPOT RETURN", date: "2026-08-20");

        var match = MatchOne(Invoice(), credit, HomeDepot);

        Assert.Equal(HomeDepot.RequestId, match.Invoice.MatchedRequestId);
        Assert.Equal(2, match.Candidates.Count);
    }

    [Fact]
    public void Should_PreferCredit_When_CustomerInvoiceHasCandidatesInBothDirections()
    {
        var deposit = Lines.Bank(Direction.Credit, 184.32m, "DEPOSIT HOME DEPOT", date: "2026-08-20");

        var match = MatchOne(Invoice(role: PartyRole.Customer), HomeDepot, deposit);

        Assert.Equal(deposit.RequestId, match.Invoice.MatchedRequestId);
    }

    [Fact]
    public void Should_PickTheBestPayee_When_SeveralCandidatesHaveTheSameDirection()
    {
        var other = Lines.Card(Direction.Debit, 184.32m, "SHELL OIL 57442", date: "2026-08-21");

        var match = MatchOne(Invoice(), other, HomeDepot);

        Assert.Equal(HomeDepot.RequestId, match.Invoice.MatchedRequestId);
    }

    [Fact]
    public void Should_UseAllCandidates_When_NoneHasThePreferredDirection()
    {
        var refund = Lines.Bank(Direction.Credit, 184.32m, "HOME DEPOT REFUND", date: "2026-08-22");
        var other = Lines.Bank(Direction.Credit, 184.32m, "ZELLE FROM J SMITH", date: "2026-08-22");

        var match = MatchOne(Invoice(), other, refund);

        Assert.Equal(refund.RequestId, match.Invoice.MatchedRequestId);
    }

    [Fact]
    public void Should_ReportAmbiguous_When_TwoCandidatesScoreTheSame()
    {
        var second = Lines.Card(Direction.Debit, 184.32m, "HOME DEPOT 0911 ORLANDO", date: "2026-08-23");

        var match = MatchOne(Invoice(), HomeDepot, second);

        Assert.False(match.Matched);
        Assert.Equal(HoldReasons.Ambiguous, match.Reason);
        Assert.Equal([HomeDepot.RequestId, second.RequestId], match.Candidates);
    }

    [Fact]
    public void Should_ReportAmbiguous_When_NoCandidateReachesTheThreshold()
    {
        var a = Lines.Bank(Direction.Debit, 184.32m, "ACH DEBIT PAYMENT 1", date: "2026-08-22");
        var b = Lines.Bank(Direction.Debit, 184.32m, "POS PURCHASE 2", date: "2026-08-22");

        var match = MatchOne(Invoice(), a, b);

        Assert.Equal(HoldReasons.Ambiguous, match.Reason);
        Assert.Contains("0.85", match.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MatchTheBestPayee_When_OtherCandidatesTieBelowTheThreshold()
    {
        var a = Lines.Bank(Direction.Debit, 184.32m, "ACH DEBIT PAYMENT 1", date: "2026-08-22");
        var b = Lines.Bank(Direction.Debit, 184.32m, "ACH DEBIT PAYMENT 2", date: "2026-08-22");

        var match = MatchOne(Invoice(), a, b, HomeDepot);

        Assert.Equal(HomeDepot.RequestId, match.Invoice.MatchedRequestId);
    }

    [Fact]
    public void Should_MatchNeither_When_TwoInvoicesClaimTheSameLine()
    {
        var first = Invoice() with { File = "a.pdf", Number = "1" };
        var second = Invoice() with { File = "b.pdf", Number = "2" };

        var matches = InvoiceMatcher.Match([first, second], [HomeDepot], Rules);

        Assert.All(matches, m =>
        {
            Assert.False(m.Matched);
            Assert.Null(m.Invoice.MatchedRequestId);
            Assert.Equal(HoldReasons.Ambiguous, m.Reason);
            Assert.Equal([HomeDepot.RequestId], m.Candidates);
        });
        Assert.Contains("b.pdf", matches[0].Note, StringComparison.Ordinal);
        Assert.Contains("a.pdf", matches[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_MatchEachInvoice_When_TheyPointAtDifferentLines()
    {
        var fpl = Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY", date: "2026-08-12");
        var power = Invoice() with { File = "fpl.pdf", Party = "FPL Electric", Total = 311.40m, Date = new DateOnly(2026, 8, 10) };

        var matches = InvoiceMatcher.Match([Invoice(), power], [HomeDepot, fpl], Rules);

        Assert.Equal([HomeDepot.RequestId, fpl.RequestId], matches.Select(m => m.Invoice.MatchedRequestId));
    }

    [Fact]
    public void Should_KeepInvoiceOrder_When_Matching()
    {
        var invoices = new[] { Invoice() with { File = "z.pdf", Total = 1m }, Invoice() with { File = "a.pdf", Total = 2m } };

        var matches = InvoiceMatcher.Match(invoices, [HomeDepot], Rules);

        Assert.Equal(["z.pdf", "a.pdf"], matches.Select(m => m.Invoice.File));
    }

    [Fact]
    public void Should_ReturnNothing_When_ThereAreNoInvoices()
    {
        Assert.Empty(InvoiceMatcher.Match([], [HomeDepot], Rules));
    }

    private static InvoiceMatch MatchOne(InvoiceFacts invoice, params StatementLine[] lines) =>
        Assert.Single(InvoiceMatcher.Match([invoice], lines, Rules));

    private static InvoiceFacts Invoice(decimal total = 184.32m, string date = "2026-08-21", PartyRole role = PartyRole.Vendor) => new()
    {
        File = "home-depot-88213.pdf",
        Party = "Home Depot",
        Role = role,
        Number = "88213",
        Date = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
        Total = total,
        CategoryHint = "plumbing fittings, repair",
    };
}
