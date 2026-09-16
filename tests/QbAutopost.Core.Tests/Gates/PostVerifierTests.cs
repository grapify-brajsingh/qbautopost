using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Gates;

/// <summary>Gate G5 (spec FR-12).</summary>
public sealed class PostVerifierTests
{
    private static readonly MappedTxn Txn = new()
    {
        Line = Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY"),
        Kind = TxnKind.Check,
        Decision = Decision.Post,
        Confidence = Confidence.Rule,
        Account = "Chase Checking 4521",
        Payee = "Florida Power & Light",
        LineAccount = "Utilities",
        RefNumber = "ACH",
    };

    private static PostResult Ok(decimal? amount = 311.40m) => new()
    {
        RequestId = Txn.RequestId, StatusCode = 0, TxnId = "1A-1", EditSequence = "99", Amount = amount,
    };

    [Fact]
    public void Should_Post_When_StatusIsZeroWithTxnIdAndSameAmount()
    {
        var result = PostVerifier.Verify([Txn], [Ok()]);

        var posted = Assert.Single(result.Posted);
        Assert.Equal("1A-1", posted.TxnId);
        Assert.Equal("99", posted.EditSequence);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Should_Post_When_ResponseEchoesNoAmount()
    {
        Assert.Single(PostVerifier.Verify([Txn], [Ok(amount: null)]).Posted);
    }

    [Fact]
    public void Should_Hold_When_StatusIsNotZero()
    {
        var result = PostVerifier.Verify([Txn], [Ok() with { StatusCode = 3140, StatusMessage = "invalid reference", TxnId = null }]);

        var held = Assert.Single(result.Rejected);
        Assert.Equal(HoldReasons.QuickBooksRejected, held.Reason);
        Assert.Contains("3140: invalid reference", held.Note, StringComparison.Ordinal);
        Assert.Equal(Decision.Hold, held.Decision);
    }

    [Fact]
    public void Should_Hold_When_TxnIdIsMissing()
    {
        var result = PostVerifier.Verify([Txn], [Ok() with { TxnId = null }]);

        Assert.Equal(HoldReasons.QuickBooksRejected, Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public void Should_HoldAndNameTxnId_When_EchoedAmountDiffers()
    {
        var result = PostVerifier.Verify([Txn], [Ok(amount: 311.41m)]);

        var held = Assert.Single(result.Rejected);
        Assert.Equal(HoldReasons.AmountMismatch, held.Reason);
        Assert.Contains("1A-1", held.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Hold_When_NoResponseMatchesRequestId()
    {
        var result = PostVerifier.Verify([Txn], [Ok() with { RequestId = "0000000000000000" }]);

        Assert.Equal(HoldReasons.QuickBooksNoResponse, Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public void Should_Hold_When_TwoResponsesShareRequestId()
    {
        var result = PostVerifier.Verify([Txn], [Ok(), Ok() with { TxnId = "1A-2" }]);

        Assert.Empty(result.Posted);
        Assert.Equal(HoldReasons.QuickBooksNoResponse, Assert.Single(result.Rejected).Reason);
    }
}
