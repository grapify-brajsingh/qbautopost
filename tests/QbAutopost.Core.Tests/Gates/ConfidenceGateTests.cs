using QbAutopost.Core.Gates;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Tests.Gates;

/// <summary>Gate G3 (spec FR-7): which model-chosen accounts may post.</summary>
public sealed class ConfidenceGateTests
{
    private const double Threshold = 0.8;
    private const string Payee = "Joe's Plumbing";
    private const string Account = "Repairs and Maintenance";

    private static readonly LedgerEntry[] History = [Entry(Payee, Account)];

    [Theory]
    [InlineData(0.8, true)]
    [InlineData(0.95, true)]
    [InlineData(1.0, true)]
    [InlineData(0.7999, false)]
    [InlineData(0.0, false)]
    public void Should_PostInvoiceTierOnlyAtOrAboveThreshold_When_ScoreIsGiven(double score, bool posts)
    {
        Assert.Equal(posts, ConfidenceGate.InvoiceMayPost(score, Threshold));
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void Should_PostModelTier_When_ScoreReachesThresholdAndPayeeWasPostedToTheAccount(double score)
    {
        Assert.Null(ConfidenceGate.CheckModel(score, Payee, Account, Threshold, History));
    }

    [Fact]
    public void Should_HoldLowConfidence_When_ModelScoreIsBelowThreshold()
    {
        Assert.Equal(HoldReasons.LowConfidence, ConfidenceGate.CheckModel(0.79, Payee, Account, Threshold, History));
    }

    [Fact]
    public void Should_HoldLowConfidence_When_ScoreIsLowAndThereIsNoHistoryEither()
    {
        Assert.Equal(HoldReasons.LowConfidence, ConfidenceGate.CheckModel(0.1, Payee, Account, Threshold, []));
    }

    [Fact]
    public void Should_HoldNoPriorPosting_When_PayeeHasNoLedgerEntries()
    {
        Assert.Equal(HoldReasons.NoPriorPosting, ConfidenceGate.CheckModel(0.99, Payee, Account, Threshold, []));
    }

    [Fact]
    public void Should_HoldNoPriorPosting_When_PayeeWasPostedOnlyToAnotherAccount()
    {
        LedgerEntry[] history = [Entry(Payee, "Utilities")];

        Assert.Equal(HoldReasons.NoPriorPosting, ConfidenceGate.CheckModel(0.99, Payee, Account, Threshold, history));
    }

    [Fact]
    public void Should_HoldNoPriorPosting_When_AnotherPayeeWasPostedToTheAccount()
    {
        LedgerEntry[] history = [Entry("Home Depot", Account)];

        Assert.Equal(HoldReasons.NoPriorPosting, ConfidenceGate.CheckModel(0.99, Payee, Account, Threshold, history));
    }

    [Fact]
    public void Should_HoldNoPriorPosting_When_TheOnlyPriorPostingWasUndone()
    {
        LedgerEntry[] history = [Entry(Payee, Account) with { Undone = true }];

        Assert.Equal(HoldReasons.NoPriorPosting, ConfidenceGate.CheckModel(0.99, Payee, Account, Threshold, history));
    }

    [Fact]
    public void Should_CountPriorPosting_When_NamesDifferOnlyInCase()
    {
        LedgerEntry[] history = [Entry("JOE'S PLUMBING", "repairs and maintenance")];

        Assert.Null(ConfidenceGate.CheckModel(0.9, Payee, Account, Threshold, history));
    }

    private static LedgerEntry Entry(string payee, string lineAccount) => new()
    {
        BatchId = "old#1",
        JobId = "old",
        Fingerprint = "fp",
        TxnId = "TX1",
        Kind = TxnKind.Check,
        Account = "Chase Checking 4521",
        Payee = payee,
        LineAccount = lineAccount,
        Amount = 100m,
        Date = new DateOnly(2026, 7, 1),
        SourceFile = "old.csv",
    };
}
