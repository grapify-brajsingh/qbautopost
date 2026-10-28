using QbAutopost.Core.Api;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Tests.Api;

/// <summary>T-1102 / dashboard v2 §6.2: a <c>post: false</c> row counts in the total, is set aside before holds, never becomes a line.</summary>
public sealed class DirectRequestReaderNotPostedTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static DirectRow Row(decimal amount, string id, int ageDays = 5) => new()
    {
        ExternalId = id,
        Kind = TxnKind.Check,
        Date = Today.AddDays(-ageDays),
        Amount = amount,
        Account = "Checking 4521",
        Payee = "ABC Plumbing LLC",
        LineAccount = "Repairs and Maintenance",
        Last4 = "4521",
    };

    private static DirectReadResult Read(DirectRequest request) => new DirectRequestReader(new DirectLimits()).Read(request, Today);

    private static DirectRequest Request(decimal? total, IReadOnlySet<int> notPosted, params DirectRow[] rows) => new()
    {
        Reference = "ref-1",
        ControlTotal = total,
        Transactions = rows,
        NotPosted = notPosted,
    };

    [Fact]
    public void Should_CountNotPostedRowInControlTotal_When_RowIsNotPosted()
    {
        var result = Read(Request(150m, new HashSet<int> { 1 }, Row(100m, "a"), Row(50m, "b")));

        Assert.Empty(result.Errors);
        Assert.Single(result.Lines);
        Assert.Equal(1, Assert.Single(result.NotPosted).Index);
    }

    [Fact]
    public void Should_RefuseControlTotalMismatch_When_TotalExcludesNotPostedRows()
    {
        var result = Read(Request(100m, new HashSet<int> { 1 }, Row(100m, "a"), Row(50m, "b")));

        Assert.StartsWith("control-total-mismatch", Assert.Single(result.Errors));
    }

    [Fact]
    public void Should_StillRefuseMalformedRow_When_RowIsNotPosted()
    {
        var result = Read(Request(100m, new HashSet<int> { 1 }, Row(100m, "a"), Row(0m, "b")));

        Assert.Contains("row 1: amount must be greater than zero", result.Errors);
    }

    [Fact]
    public void Should_SetAsideBeforeHolds_When_NotPostedRowIsTooOld()
    {
        var result = Read(Request(150m, new HashSet<int> { 1 }, Row(100m, "a"), Row(50m, "b", ageDays: 800)));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Held);
        Assert.Equal(1, Assert.Single(result.NotPosted).Index);
    }

    [Fact]
    public void Should_RefuseRequest_When_EveryRowIsNotPosted()
    {
        var result = Read(Request(150m, new HashSet<int> { 0, 1 }, Row(100m, "a"), Row(50m, "b")));

        Assert.StartsWith("nothing-to-post:", Assert.Single(result.Errors));
    }

    [Fact]
    public void Should_IgnoreOutOfRangeIndex_When_CheckingEveryRowIsNotPosted()
    {
        var result = Read(Request(100m, new HashSet<int> { 5 }, Row(100m, "a")));

        Assert.Empty(result.Errors);
        Assert.Single(result.Lines);
        Assert.Empty(result.NotPosted);
    }

    [Fact]
    public void Should_BehaveAsBefore_When_NotPostedIsEmpty()
    {
        var withDefault = Read(new DirectRequest
        {
            Reference = "ref-1",
            ControlTotal = 150m,
            Transactions = [Row(100m, "a"), Row(50m, "b", ageDays: 800)],
        });
        var withEmpty = Read(Request(150m, new HashSet<int>(), Row(100m, "a"), Row(50m, "b", ageDays: 800)));

        Assert.Equal(withDefault.Errors, withEmpty.Errors);
        Assert.Equal(withDefault.Lines, withEmpty.Lines);
        Assert.Equal(withDefault.Held, withEmpty.Held);
        Assert.Empty(withEmpty.NotPosted);
        Assert.Single(withEmpty.Held);
        Assert.Single(withEmpty.Lines);
    }
}
