using QbAutopost.Core.Api;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Core.Tests.Api;

/// <summary>
/// T-907 / FR-A-6: the offline dry run. The planner runs the direct rows through the same mapper (FR-6), the same
/// in-batch and ledger duplicate checks (G4) and the same qbXML builder (FR-9) the folder path uses, and stops short
/// of QuickBooks — names come from <c>qb-lists.json</c>, duplicates from <c>ledger.json</c>.
/// <para>
/// The property these tests exist to protect: <b>a plan that says "would post" must not become a post that holds</b>.
/// Every decision the post will make, except the SDK call itself, is made here.
/// </para>
/// </summary>
public sealed class DirectPlannerTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static readonly Rules BaseRules = new()
    {
        VendorAccounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ABC Plumbing LLC"] = "Repairs and Maintenance",
        },
        PayeeAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ABC PLUMBING"] = "ABC Plumbing LLC",
        },
        SkipPatterns = ["PAYMENT THANK YOU"],
        HoldingExpenseAccount = "Ask My Accountant",
    };

    private static DirectRow Row(
        decimal amount = 184.32m,
        TxnKind kind = TxnKind.Check,
        string? externalId = "row-1",
        string account = "Chase Checking 4521",
        string? lineAccount = null,
        string? payee = "ABC PLUMBING 24HR",
        string? memo = null,
        string? checkNo = null,
        string last4 = "4521") =>
        new()
        {
            ExternalId = externalId,
            Kind = kind,
            Date = Today.AddDays(-10),
            Amount = amount,
            Account = account,
            LineAccount = lineAccount,
            Payee = payee,
            CheckNo = checkNo,
            Memo = memo ?? "ABC PLUMBING 24HR SERVICE",
            Last4 = last4,
        };

    private static DirectRequest Request(params DirectRow[] rows) => new()
    {
        Reference = "payroll-2026-09",
        ControlTotal = rows.Sum(r => r.Amount),
        Transactions = rows,
    };

    private static DirectPlan Plan(
        DirectRequest request, Rules? rules = null, QbLists? lists = null, Ledger? ledger = null) =>
        new DirectPlanner(new DirectLimits()).Plan(
            request, rules ?? BaseRules, lists ?? QbLists.Empty, ledger ?? Ledger.Empty, Today);

    [Fact]
    public void Should_PlanARowAsWouldPost_When_RulesResolveIt()
    {
        var plan = Plan(Request(Row()));

        Assert.True(plan.Ok);
        Assert.Empty(plan.Errors);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.WouldPost, row.Outcome);
        Assert.Equal(TxnKind.Check, row.Kind);
        // The header account is the caller's; the line account came from the vendor rule (tier 1).
        Assert.Equal("Chase Checking 4521", row.Account);
        Assert.Equal("Repairs and Maintenance", row.LineAccount);
        Assert.Equal("ABC Plumbing LLC", row.Payee);
        Assert.Equal("row-1", row.ExternalId);
        Assert.Equal(16, row.RequestId!.Length);
    }

    [Fact]
    public void Should_UseTheCallersAccount_When_NoRuleMapsTheLastFour()
    {
        // The folder path finds the bank account by last4 in rules.json; a direct caller names it instead, so a last4
        // no rule knows must not hold the row (that would make the API unusable without editing rules.json first).
        var plan = Plan(Request(Row(last4: "9999")));

        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.WouldPost, row.Outcome);
        Assert.Equal("Chase Checking 4521", row.Account);
    }

    [Fact]
    public void Should_PreferTheCallersLineAccount_When_OneIsGiven()
    {
        var plan = Plan(Request(Row(lineAccount: "Utilities")));

        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.WouldPost, row.Outcome);
        Assert.Equal("Utilities", row.LineAccount);
    }

    [Fact]
    public void Should_HoldTheRow_When_NoRuleAndNoLineAccountResolveIt()
    {
        // allowModelAccounts is false, so tiers 3-4 never run: an unresolved row is held, never guessed (Q-51).
        var plan = Plan(Request(Row(payee: "NEVER SEEN BEFORE", memo: "NEVER SEEN BEFORE LLC")));

        Assert.False(plan.Ok);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.Held, row.Outcome);
        Assert.Equal(HoldReasons.NoAccountRule, row.Reason);
    }

    [Fact]
    public void Should_PlanTheRow_When_TheCallerNamesBothAPayeeAndALineAccountNoRuleKnows()
    {
        // The caller is explicit about both names, so there is nothing to guess: it is their instruction, and an
        // account QuickBooks does not know is caught by the lists check below and by G5 at post time.
        var plan = Plan(Request(Row(payee: "NEVER SEEN BEFORE", memo: "NEVER SEEN BEFORE LLC", lineAccount: "Utilities")));

        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.WouldPost, row.Outcome);
        Assert.Equal("NEVER SEEN BEFORE", row.Payee);
        Assert.Equal("Utilities", row.LineAccount);
    }

    [Fact]
    public void Should_HoldOneRowOnly_When_TheReaderRefusesIt()
    {
        var plan = Plan(Request(Row(), Row(amount: 200000m, externalId: "row-2", memo: "OTHER")));

        Assert.False(plan.Ok);
        var held = Assert.Single(plan.Rows, r => r.Outcome == DirectOutcome.Held);
        Assert.Equal("row-2", held.ExternalId);
        Assert.Contains("amount-over-limit", held.Reason);
        // One bad line does not fail the batch: the other row still plans.
        Assert.Single(plan.Rows, r => r.Outcome == DirectOutcome.WouldPost);
    }

    [Fact]
    public void Should_RefuseTheWholeBatch_When_TheControlTotalDoesNotMatch()
    {
        var plan = Plan(new DirectRequest { ControlTotal = 1m, Transactions = [Row()] });

        Assert.False(plan.Ok);
        Assert.Contains(plan.Errors, e => e.Contains("control-total-mismatch"));
        // Nothing is planned out of a refused batch: a partial plan invites a partial post.
        Assert.Empty(plan.Rows);
        Assert.Equal(string.Empty, plan.QbXml);
    }

    [Fact]
    public void Should_MarkARowDuplicate_When_TheLedgerAlreadyHasItsFingerprint()
    {
        var first = Plan(Request(Row()));
        var fingerprint = Assert.Single(first.ToPost).Line.Fingerprint;
        var ledger = new Ledger
        {
            Posted =
            [
                new LedgerEntry
                {
                    BatchId = "2026-09-tropicana#1", JobId = "2026-09-tropicana", Fingerprint = fingerprint,
                    TxnId = "8A1-1234", Kind = TxnKind.Check, Account = "Chase Checking 4521",
                    Amount = 184.32m, Date = Today.AddDays(-10), SourceFile = "statement.csv",
                },
            ],
        };

        var plan = Plan(Request(Row()), ledger: ledger);

        Assert.False(plan.Ok);
        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.Duplicate, row.Outcome);
        Assert.Equal(HoldReasons.AlreadyPosted, row.Reason);
        // The operator needs to know where it was posted before, not just that it was.
        Assert.Equal("2026-09-tropicana#1", row.BatchId);
        Assert.Empty(plan.ToPost);
    }

    [Fact]
    public void Should_HoldBothRows_When_TwoRowsShareAFingerprint()
    {
        // Identical rows share a request id, so posting both would post one transaction twice under one id.
        var plan = Plan(Request(Row(), Row(externalId: "row-2")));

        Assert.Equal(2, plan.Rows.Count(r => r.Reason == HoldReasons.DuplicateLine));
        Assert.Empty(plan.ToPost);
    }

    [Fact]
    public void Should_ReportAnUnknownAccount_When_TheListsDoNotHaveIt()
    {
        var lists = new QbLists { Accounts = [new QbAccount { Name = "Chase Checking 4521" }] };

        var plan = Plan(Request(Row(lineAccount: "Repairs & Maint.")), lists: lists);

        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.Held, row.Outcome);
        Assert.Equal(HoldReasons.UnknownAccount, row.Reason);
        Assert.Contains("Repairs & Maint.", plan.UnknownNames.Accounts);
        // The header account is known, so it must not be reported as missing.
        Assert.DoesNotContain("Chase Checking 4521", plan.UnknownNames.Accounts);
    }

    [Fact]
    public void Should_CheckNamesOnlyAgainstListsThatExist_When_TheListsAreEmpty()
    {
        // A missing qb-lists.json is a warning, not a refusal (T-902): a batch every rule resolves still posts.
        var plan = Plan(Request(Row(lineAccount: "Repairs & Maint.")), lists: QbLists.Empty);

        Assert.True(plan.Ok);
        Assert.Empty(plan.UnknownNames.Accounts);
    }

    [Fact]
    public void Should_ReportAnUnknownVendor_When_TheCallerNamesOneTheListsDoNotHave()
    {
        var lists = new QbLists
        {
            Accounts = [new QbAccount { Name = "Chase Checking 4521" }, new QbAccount { Name = "Utilities" }],
            Vendors = ["Florida Power & Light"],
        };

        var plan = Plan(Request(Row(lineAccount: "Utilities")), lists: lists);

        var row = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.Held, row.Outcome);
        Assert.Equal(HoldReasons.UnknownPayee, row.Reason);
        Assert.Contains("ABC Plumbing LLC", plan.UnknownNames.Vendors);
    }

    [Fact]
    public void Should_CountADepositsPayeeAsACustomer_When_TheListsAreChecked()
    {
        var lists = new QbLists
        {
            Accounts = [new QbAccount { Name = "Chase Checking 4521" }, new QbAccount { Name = "Rental Income" }],
            Customers = ["Unit 4 Tenant"],
        };
        var row = Row(kind: TxnKind.Deposit, payee: "SOMEONE ELSE", memo: "SOMEONE ELSE RENT", lineAccount: "Rental Income");

        var plan = Plan(Request(row), lists: lists);

        Assert.Contains("SOMEONE ELSE", plan.UnknownNames.Customers);
        Assert.Empty(plan.UnknownNames.Vendors);
    }

    [Fact]
    public void Should_SkipTheRow_When_ARuleSaysToSkipIt()
    {
        var row = Row(kind: TxnKind.CcCredit, memo: "PAYMENT THANK YOU", payee: null, last4: "8812");

        var plan = Plan(Request(row));

        var planned = Assert.Single(plan.Rows);
        Assert.Equal(DirectOutcome.Skipped, planned.Outcome);
        // A skip is a rule doing its job, not a problem to report: the batch is still clean.
        Assert.True(plan.Ok);
        Assert.Empty(plan.ToPost);
    }

    [Fact]
    public void Should_HoldRows_When_OneLastFourNamesTwoAccounts()
    {
        var plan = Plan(Request(Row(), Row(externalId: "row-2", account: "Chase Savings 4521", memo: "OTHER")));

        Assert.Equal(2, plan.Rows.Count(r => r.Reason == HoldReasons.ConflictingLast4));
    }

    [Fact]
    public void Should_BuildQbXmlForThePostableRowsOnly_When_SomeAreHeld()
    {
        var plan = Plan(Request(Row(), Row(amount: 200000m, externalId: "row-2", memo: "OTHER")));

        Assert.Single(plan.ToPost);
        Assert.Contains("<CheckAddRq", plan.QbXml);
        Assert.Equal(1, plan.QbXmlCounts["CheckAdd"]);
    }

    [Fact]
    public void Should_TotalWhatWasSubmittedAndWhatWouldPost_When_SomeRowsAreHeld()
    {
        var plan = Plan(Request(Row(), Row(amount: 200000m, externalId: "row-2", memo: "OTHER")));

        Assert.Equal(200184.32m, plan.SubmittedTotal);
        Assert.Equal(184.32m, plan.WouldPostTotal);
    }
}
