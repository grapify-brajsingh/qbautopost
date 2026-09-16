using System.Text.Json.Nodes;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Hermes;

/// <summary>T3 answer validation (spec §9.3): total &gt; 0, role ∈ {vendor, customer}.</summary>
public sealed class InvoiceAnswerTests
{
    [Fact]
    public void Should_AcceptFixtureAnswer_When_ItMatchesTheSchema()
    {
        var answer = Parse(Fixtures.Read("hermes", "invoice.json"), out var errors);

        Assert.Empty(errors);
        Assert.Equal(PartyRole.Vendor, answer!.PartyRole);
    }

    [Fact]
    public void Should_ConvertToFacts_When_AnswerIsValid()
    {
        var answer = Parse(Fixtures.Read("hermes", "invoice.json"), out _);

        var facts = answer!.ToFacts("home-depot-88213.pdf");

        Assert.Equal(
            new InvoiceFacts
            {
                File = "home-depot-88213.pdf",
                Party = "Home Depot",
                Role = PartyRole.Vendor,
                Number = "88213",
                Date = new DateOnly(2026, 8, 21),
                Total = 184.32m,
                CategoryHint = "plumbing fittings, repair",
            },
            facts);
    }

    [Fact]
    public void Should_TrimTextAndDropBlankHint_When_ConvertingToFacts()
    {
        var answer = Parse(Mutate(a =>
        {
            a["party"] = "  Home Depot ";
            a["number"] = " 88213 ";
            a["categoryHint"] = "   ";
            a["date"] = null;
        }), out _);

        var facts = answer!.ToFacts("x.pdf");

        Assert.Equal(("Home Depot", "88213", (string?)null, (DateOnly?)null), (facts.Party, facts.Number, facts.CategoryHint, facts.Date));
    }

    [Theory]
    [InlineData("VENDOR", PartyRole.Vendor)]
    [InlineData("Customer", PartyRole.Customer)]
    public void Should_AcceptRole_When_CaseDiffers(string role, PartyRole expected)
    {
        var answer = Parse(Mutate(a => a["role"] = role), out var errors);

        Assert.Empty(errors);
        Assert.Equal(expected, answer!.PartyRole);
    }

    [Theory]
    [InlineData("supplier")]
    [InlineData("")]
    [InlineData(null)]
    public void Should_RejectRole_When_ItIsNotVendorOrCustomer(string? role)
    {
        var errors = Errors(a => a["role"] = role);

        Assert.Contains(errors, e => e.StartsWith("role", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-184.32)]
    public void Should_RejectTotal_When_ItIsNotPositive(double total)
    {
        var errors = Errors(a => a["total"] = (decimal)total);

        Assert.Contains(errors, e => e.StartsWith("total", StringComparison.Ordinal) && e.Contains("greater than 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectTotal_When_ItIsMissing()
    {
        var errors = Errors(a => a.Remove("total"));

        Assert.Contains(errors, e => e.StartsWith("total", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectTotal_When_ItHasMoreThanTwoDecimals()
    {
        var errors = Errors(a => a["total"] = 184.325m);

        Assert.Contains(errors, e => e.StartsWith("total", StringComparison.Ordinal) && e.Contains("2 decimal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Should_RejectParty_When_ItIsMissingOrBlank(string? party)
    {
        var errors = Errors(a => a["party"] = party);

        Assert.Contains(errors, e => e.StartsWith("party", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("08/21/2026")]
    [InlineData("2026-02-30")]
    [InlineData("")]
    public void Should_RejectDate_When_ItIsNotAnIsoDate(string date)
    {
        var errors = Errors(a => a["date"] = date);

        Assert.Contains(errors, e => e.StartsWith("date", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_AcceptMissingOptionalFields_When_InvoiceDoesNotShowThem()
    {
        var answer = Parse(Mutate(a =>
        {
            a["number"] = null;
            a["date"] = null;
            a.Remove("categoryHint");
        }), out var errors);

        Assert.Empty(errors);
        Assert.Null(answer!.CategoryHint);
    }

    [Fact]
    public void Should_RejectNumber_When_ItIsBlank()
    {
        var errors = Errors(a => a["number"] = " ");

        Assert.Contains(errors, e => e.StartsWith("number", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_ListEveryError_When_SeveralFieldsAreWrong()
    {
        var errors = Errors(a =>
        {
            a["role"] = "payee";
            a["total"] = 0;
            a["party"] = "";
        });

        Assert.Equal(3, errors.Count);
    }

    private static IReadOnlyList<string> Errors(Action<JsonObject> mutate)
    {
        Parse(Mutate(mutate), out var errors);
        return errors;
    }

    private static string Mutate(Action<JsonObject> mutate)
    {
        var answer = JsonNode.Parse(Fixtures.Read("hermes", "invoice.json"))!.AsObject();
        mutate(answer);
        return answer.ToJsonString();
    }

    private static InvoiceAnswer? Parse(string json, out IReadOnlyList<string> errors) =>
        JsonReply.TryParse<InvoiceAnswer>(json, out errors);
}
