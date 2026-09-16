using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

/// <summary>One test per row of the spec FR-6 routing table, plus the header-account and payee holds.</summary>
public sealed class MapperRoutingTests
{
    private readonly Mapper _mapper = new(Fixtures.SampleRules());

    // FR-6 row 1: card line matches SkipPatterns → Skip
    [Fact]
    public void Should_Skip_When_CardLineMatchesSkipPattern()
    {
        var txn = _mapper.Map(Lines.Card(Direction.Credit, 1500m, "AUTOMATIC PAYMENT - THANK YOU"));

        Assert.Equal(TxnKind.Skip, txn.Kind);
        Assert.Equal(Decision.Skip, txn.Decision);
        Assert.Null(txn.LineAccount);
    }

    // FR-6 row 2: card debit → CcCharge, ACH, vendor, tiers
    [Fact]
    public void Should_MapToCcChargeWithVendor_When_CardLineIsDebit()
    {
        var txn = _mapper.Map(Lines.Card(Direction.Debit, 62.18m, "AMAZON.COM*RT4Y1 AMZN.COM/BILL"));

        Assert.Equal(TxnKind.CcCharge, txn.Kind);
        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("ACH", txn.RefNumber);
        Assert.Equal("Amazon", txn.Payee);
        Assert.Equal("Chase Sapphire 7788", txn.Account);
        Assert.Equal("Office Supplies", txn.LineAccount);
        Assert.Equal(Confidence.Rule, txn.Confidence);
        Assert.Equal(1, txn.Tier);
    }

    // FR-6 row 3: card credit → CcCredit, ACH, vendor, tiers
    [Fact]
    public void Should_MapToCcCreditWithVendor_When_CardLineIsCredit()
    {
        var txn = _mapper.Map(Lines.Card(Direction.Credit, 15.99m, "AMAZON.COM*RF8K2 AMZN.COM/BILL"));

        Assert.Equal(TxnKind.CcCredit, txn.Kind);
        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("ACH", txn.RefNumber);
        Assert.Equal("Amazon", txn.Payee);
        Assert.Equal("Office Supplies", txn.LineAccount);
    }

    // FR-6 row 4: bank credit → Deposit, customer, DepositIncomeAccount
    [Fact]
    public void Should_MapToDepositWithCustomer_When_BankLineIsCredit()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Credit, 2400m, "DEPOSIT PALM COURT RENTALS"));

        Assert.Equal(TxnKind.Deposit, txn.Kind);
        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Null(txn.RefNumber);
        Assert.Equal("Palm Court Rentals LLC", txn.Payee);
        Assert.Equal("Rental Income", txn.LineAccount);
        Assert.Equal("Chase Checking 4521", txn.Account);
    }

    // FR-6 row 5: bank debit matching TransferPatterns → Check, checkNo ?? ACH, no payee, pattern account
    [Fact]
    public void Should_MapToCheckWithoutPayee_When_BankDebitMatchesTransferPattern()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 1500m, "ONLINE TRANSFER TO CHASE CARD 7788"));

        Assert.Equal(TxnKind.Check, txn.Kind);
        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("ACH", txn.RefNumber);
        Assert.Null(txn.Payee);
        Assert.Equal("Chase Sapphire 7788", txn.LineAccount);
    }

    // FR-6 row 6: bank debit with check number → Check, checkNo, no payee, keyword ?? HoldingExpenseAccount
    [Fact]
    public void Should_MapToHoldingAccountWithoutPayee_When_BankDebitHasCheckNumber()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 420m, "CHECK 1043", checkNo: "1043"));

        Assert.Equal(TxnKind.Check, txn.Kind);
        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("1043", txn.RefNumber);
        Assert.Null(txn.Payee);
        Assert.Equal("Ask My Accountant", txn.LineAccount);
        Assert.Equal(Confidence.Holding, txn.Confidence);
    }

    // FR-6 row 7: bank debit without check number → Check, ACH, vendor, tiers
    [Fact]
    public void Should_MapToAchCheckWithVendor_When_BankDebitHasNoCheckNumber()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY"));

        Assert.Equal(TxnKind.Check, txn.Kind);
        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("ACH", txn.RefNumber);
        Assert.Equal("Florida Power & Light", txn.Payee);
        Assert.Equal("Utilities", txn.LineAccount);
    }

    [Fact]
    public void Should_UseKeywordRule_When_NumberedCheckDescriptionMatchesKeyword()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 40m, "CHECK 1044 SHELL", checkNo: "1044"));

        Assert.Equal("Automobile Expense", txn.LineAccount);
        Assert.Equal(Confidence.Rule, txn.Confidence);
        Assert.Null(txn.Payee);
    }

    [Fact]
    public void Should_HoldAsUnknownAccount_When_LastFourIsNotInRules()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY", last4: "0000"));

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.UnknownAccount, txn.Reason);
    }

    [Fact]
    public void Should_HoldWithCandidates_When_VendorIsUnknown()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 3199.70m, "ACH DEBIT UNKNOWN PLUMBER SVC"));

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
        Assert.Equal(3, txn.Candidates.Count);
    }

    [Fact]
    public void Should_HoldAsUnknownPayee_When_DepositCustomerIsUnknown()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Credit, 250m, "DEPOSIT SUNRISE PROPERTY MGMT"));

        Assert.Equal(TxnKind.Deposit, txn.Kind);
        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
    }

    [Fact]
    public void Should_Hold_When_CheckNumberIsLongerThanElevenCharacters()
    {
        var txn = _mapper.Map(Lines.Bank(Direction.Debit, 420m, "CHECK", checkNo: "123456789012"));

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.RefNumberTooLong, txn.Reason);
    }

    [Fact]
    public void Should_HoldNumberedCheck_When_NoHoldingAccountIsConfigured()
    {
        var mapper = new Mapper(Fixtures.SampleRules() with { HoldingExpenseAccount = null });

        var txn = mapper.Map(Lines.Bank(Direction.Debit, 420m, "CHECK 1043", checkNo: "1043"));

        Assert.Equal(HoldReasons.NoHoldingAccount, txn.Reason);
    }

    [Fact]
    public void Should_NeverChangeTheAmount_When_Mapping()
    {
        var line = Lines.Bank(Direction.Debit, 311.40m, "ACH DEBIT FPL ELECTRIC UTILITY");

        Assert.Same(line, _mapper.Map(line).Line);
    }
}
