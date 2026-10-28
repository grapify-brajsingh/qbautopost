using QbAutopost.Core.Api;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Tests.Api;

/// <summary>T-1102 / dashboard v2 §6.2: the planner reports a <c>post: false</c> row as Skipped and keeps it out of qbXML.</summary>
public sealed class DirectPlannerNotPostedTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static DirectRow Row(decimal amount, string id, string account = "Chase Checking 4521", string memo = "plain memo") => new()
    {
        ExternalId = id,
        Kind = TxnKind.Check,
        Date = Today.AddDays(-10),
        Amount = amount,
        Account = account,
        LineAccount = "Repairs and Maintenance",
        Payee = "ABC Plumbing LLC",
        Memo = memo,
        Last4 = "4521",
    };

    private static DirectPlan Plan(IReadOnlySet<int> notPosted, QbLists? lists = null, params DirectRow[] rows) =>
        new DirectPlanner(new DirectLimits()).Plan(
            new DirectRequest { ControlTotal = rows.Sum(r => r.Amount), Transactions = rows, NotPosted = notPosted },
            new Rules(),
            lists ?? QbLists.Empty,
            Ledger.Empty,
            Today);

    [Fact]
    public void Should_ReportNotPostedRowAsSkipped_When_RowIsNotPosted()
    {
        var plan = Plan(new HashSet<int> { 1 }, null, Row(100m, "a"), Row(50m, "b"));

        Assert.True(plan.Ok);
        var row = plan.Rows[1];
        Assert.Equal(DirectOutcome.Skipped, row.Outcome);
        Assert.Equal("not-to-post", row.Reason);
        Assert.Equal(TxnKind.Check, row.Kind);
        Assert.Equal(50m, row.Amount);
        Assert.Equal("b", row.ExternalId);
        Assert.Equal(DirectOutcome.WouldPost, plan.Rows[0].Outcome);
    }

    [Fact]
    public void Should_NotHoldNotPostedRow_When_RowWouldBeHeldOtherwise()
    {
        var lists = new QbLists
        {
            Accounts = [new QbAccount { Name = "Chase Checking 4521" }, new QbAccount { Name = "Repairs and Maintenance" }],
        };

        var plan = Plan(new HashSet<int> { 1 }, lists, Row(100m, "a"), Row(50m, "b", account: "No Such Account"));

        Assert.Equal(DirectOutcome.Skipped, plan.Rows[1].Outcome);
        Assert.Equal(0, plan.Held);
        Assert.True(plan.Ok);
    }

    [Fact]
    public void Should_LeaveNotPostedRowOutOfQbXml_When_Planned()
    {
        var plan = Plan(new HashSet<int> { 1 }, null, Row(100m, "a", memo: "posted memo"), Row(77.77m, "b", memo: "secret memo"));

        Assert.DoesNotContain("77.77", plan.QbXml);
        Assert.DoesNotContain("secret memo", plan.QbXml);
        Assert.Contains("100.00", plan.QbXml);
        Assert.Equal(1, plan.QbXmlCounts.Values.Sum());
    }

    [Fact]
    public void Should_NotFlagDuplicateLine_When_IdenticalTwinIsNotPosted()
    {
        var plan = Plan(new HashSet<int> { 1 }, null, Row(100m, "a"), Row(100m, "a"));

        Assert.Equal(DirectOutcome.WouldPost, plan.Rows[0].Outcome);
        Assert.Equal(DirectOutcome.Skipped, plan.Rows[1].Outcome);
    }
}
