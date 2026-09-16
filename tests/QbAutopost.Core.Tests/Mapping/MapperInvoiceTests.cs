using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

/// <summary>
/// FR-5/FR-6: a matched invoice sets <see cref="MappedTxn.InvoiceRef"/> and supplies the payee when the line needs one and
/// its description names none. The party must resolve to a known name; the invoice role must fit the line.
/// </summary>
public sealed class MapperInvoiceTests
{
    private static readonly QbLists Lists = new() { Vendors = ["Joe's Plumbing"], Customers = ["Sunrise Property Management"] };

    private static readonly StatementLine Plumber =
        Lines.Bank(Direction.Debit, 3199.70m, "ACH DEBIT UNKNOWN PLUMBER SVC", date: "2026-08-15");

    private static readonly StatementLine Sunrise =
        Lines.Bank(Direction.Credit, 250.00m, "DEPOSIT SUNRISE PROPERTY MGMT", date: "2026-08-28");

    private static readonly Rules Rules = Fixtures.SampleRules() with
    {
        VendorAccounts = new Dictionary<string, string>(Fixtures.SampleRules().VendorAccounts, StringComparer.OrdinalIgnoreCase)
        {
            ["Joe's Plumbing"] = "Repairs and Maintenance",
        },
    };

    [Fact]
    public void Should_HoldUnknownPayee_When_LineHasNoInvoice()
    {
        var txn = Map(Plumber);

        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
        Assert.Null(txn.InvoiceRef);
    }

    [Fact]
    public void Should_SupplyVendorAndPost_When_MatchedVendorInvoiceNamesAKnownVendor()
    {
        var txn = Map(Plumber, Invoice(Plumber, "Joe's Plumbing"));

        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("Joe's Plumbing", txn.Payee);
        Assert.Equal("Repairs and Maintenance", txn.LineAccount);
        Assert.Equal(1, txn.Tier);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
        Assert.Contains("payee from invoice joes-invoice.pdf", txn.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_UseTheKnownSpelling_When_InvoicePartyIsCloseToAKnownVendor()
    {
        var txn = Map(Plumber, Invoice(Plumber, "JOES PLUMBING"));

        Assert.Equal("Joe's Plumbing", txn.Payee);
    }

    [Fact]
    public void Should_UseAnAlias_When_InvoicePartyContainsOne()
    {
        var line = Lines.Card(Direction.Debit, 97.98m, "POS 88213 NOIDA", date: "2026-08-21");

        var txn = Map(line, Invoice(line, "The Home Depot Inc."));

        Assert.Equal("Home Depot", txn.Payee);
        Assert.Equal(Decision.Post, txn.Decision);
    }

    [Fact]
    public void Should_HoldUnknownPayeeWithInvoiceRef_When_InvoicePartyIsNotAKnownName()
    {
        var txn = Map(Plumber, Invoice(Plumber, "Acme Pipe Works"));

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
        Assert.Null(txn.Payee);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
        Assert.Contains("Acme Pipe Works", txn.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_KeepTheDescriptionPayee_When_InvoiceNamesAnotherVendor()
    {
        var line = Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22");

        var txn = Map(line, Invoice(line, "Joe's Plumbing"));

        Assert.Equal("Home Depot", txn.Payee);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
        Assert.Null(txn.Note);
    }

    [Fact]
    public void Should_NotSupplyVendor_When_InvoiceIsACustomerInvoice()
    {
        var txn = Map(Plumber, Invoice(Plumber, "Joe's Plumbing", PartyRole.Customer));

        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
    }

    [Fact]
    public void Should_SupplyCustomer_When_DepositMatchesACustomerInvoice()
    {
        var txn = Map(Sunrise, Invoice(Sunrise, "Sunrise Property Management", PartyRole.Customer));

        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal("Sunrise Property Management", txn.Payee);
        Assert.Equal("Rental Income", txn.LineAccount);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
    }

    [Fact]
    public void Should_NotSupplyCustomer_When_DepositMatchesAVendorInvoice()
    {
        var txn = Map(Sunrise, Invoice(Sunrise, "Sunrise Property Management"));

        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
        Assert.Null(txn.Payee);
    }

    [Fact]
    public void Should_LeavePayeeBlank_When_NumberedCheckMatchesAnInvoice()
    {
        var check = Lines.Bank(Direction.Debit, 420.00m, "CHECK 1043", checkNo: "1043", date: "2026-08-05");

        var txn = Map(check, Invoice(check, "Joe's Plumbing"));

        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Null(txn.Payee);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
    }

    [Fact]
    public void Should_LeavePayeeBlank_When_TransferMatchesAnInvoice()
    {
        var transfer = Lines.Bank(Direction.Debit, 1500m, "ONLINE TRANSFER TO CHASE CARD 7788", date: "2026-08-02");

        var txn = Map(transfer, Invoice(transfer, "Joe's Plumbing"));

        Assert.Null(txn.Payee);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
    }

    [Fact]
    public void Should_SetInvoiceRef_When_MatchedLineIsSkipped()
    {
        var payment = Lines.Card(Direction.Credit, 1500m, "AUTOMATIC PAYMENT - THANK YOU", date: "2026-08-20");

        var txn = Map(payment, Invoice(payment, "Chase"));

        Assert.Equal(Decision.Skip, txn.Decision);
        Assert.Equal("joes-invoice.pdf", txn.InvoiceRef);
    }

    [Fact]
    public void Should_IgnoreUnmatchedInvoices_When_Mapping()
    {
        var unmatched = Invoice(Plumber, "Joe's Plumbing") with { MatchedRequestId = null };

        var txn = Map(Plumber, unmatched);

        Assert.Null(txn.InvoiceRef);
        Assert.Equal(HoldReasons.UnknownPayee, txn.Reason);
    }

    [Fact]
    public void Should_NotChangeTheAmount_When_InvoiceTotalDiffersByHalfACent()
    {
        var invoice = Invoice(Plumber, "Joe's Plumbing") with { Total = 3199.705m };

        var txn = Map(Plumber, invoice);

        Assert.Equal(3199.70m, txn.Line.Amount);
    }

    private static MappedTxn Map(StatementLine line, params InvoiceFacts[] invoices) =>
        new Mapper(Rules, Lists, [], invoices).Map(line);

    private static InvoiceFacts Invoice(StatementLine line, string party, PartyRole role = PartyRole.Vendor) => new()
    {
        File = "joes-invoice.pdf",
        Party = party,
        Role = role,
        Date = line.Date,
        Total = line.Amount,
        MatchedRequestId = line.RequestId,
    };
}
