using QbAutopost.Core.Api;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Tests.Api;

/// <summary>
/// T-906 / FR-A-8, FR-A-9: JSON rows become the same <see cref="StatementLine"/>s a statement produces, so the direct
/// path goes through the same mapper, duplicate gate and qbXML builder. Money is never adjusted here: a control total
/// that does not match refuses the batch (CLAUDE.md rule 3).
/// </summary>
public sealed class DirectRequestReaderTests
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static readonly DirectLimits Limits = new()
    {
        MaxRows = 3,
        MaxLineAmount = 1000m,
        MaxAgeDays = 730,
        MaxFutureDays = 1,
    };

    private static DirectRow Row(
        decimal amount = 100m,
        TxnKind kind = TxnKind.Check,
        string? date = null,
        string? externalId = "row-1",
        string account = "Checking 4521") =>
        new()
        {
            ExternalId = externalId,
            Kind = kind,
            Date = date is null ? Today.AddDays(-5) : DateOnly.Parse(date),
            Amount = amount,
            Account = account,
            Payee = "ABC Plumbing LLC",
            LineAccount = "Repairs and Maintenance",
            CheckNo = "1042",
            Memo = "INV 88213",
            Last4 = "4521",
        };

    private static DirectRequest Request(params DirectRow[] rows) =>
        new()
        {
            Reference = "payroll-2026-09",
            ControlTotal = rows.Sum(r => r.Amount),
            Transactions = rows,
        };

    private static DirectReadResult Read(DirectRequest request) => new DirectRequestReader(Limits).Read(request, Today);

    [Fact]
    public void Should_ProduceOneLinePerRow_When_TheRequestIsValid()
    {
        var result = Read(Request(Row(), Row(externalId: "row-2")));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Held);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal([1, 2], result.Lines.Select(l => l.Line.LineNo));
        Assert.Equal(["row-1", "row-2"], result.Lines.Select(l => l.ExternalId));
    }

    [Theory]
    [InlineData(TxnKind.Check, SourceKind.Bank, Direction.Debit)]
    [InlineData(TxnKind.Deposit, SourceKind.Bank, Direction.Credit)]
    [InlineData(TxnKind.CcCharge, SourceKind.Card, Direction.Debit)]
    [InlineData(TxnKind.CcCredit, SourceKind.Card, Direction.Credit)]
    public void Should_DeriveSourceAndDirection_When_KindIsGiven(TxnKind kind, SourceKind source, Direction direction)
    {
        // The caller never states the direction: two callers describing one movement must fingerprint alike (FR-A-8).
        var line = Assert.Single(Read(Request(Row(kind: kind))).Lines).Line;

        Assert.Equal(source, line.Kind);
        Assert.Equal(direction, line.Direction);
    }

    [Fact]
    public void Should_GiveTheSameFingerprint_When_TwoRequestsDescribeTheSameMovement()
    {
        var first = Assert.Single(Read(Request(Row())).Lines).Line;
        var second = Assert.Single(Read(Request(Row(externalId: "a-different-caller-id"))).Lines).Line;

        // externalId is the caller's own key and must not change identity, or G4 would miss the repeat.
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint[..16], second.Fingerprint[..16]);
    }

    [Fact]
    public void Should_Refuse_When_TheControlTotalDoesNotMatch()
    {
        var request = Request(Row(100m), Row(50m)) with { ControlTotal = 149.99m };

        var result = Read(request);

        Assert.Contains(result.Errors, e => e.Contains("control-total-mismatch", StringComparison.Ordinal));
        // Rule 3: nothing is posted and no amount is "fixed" to make the total agree.
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Should_Refuse_When_TheControlTotalIsMissing()
    {
        var request = Request(Row()) with { ControlTotal = null };

        Assert.Contains(Read(request).Errors, e => e.Contains("controlTotal", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Refuse_When_ThereAreTooManyRows()
    {
        var request = Request(Row(), Row(), Row(), Row());

        Assert.Contains(Read(request).Errors, e => e.Contains("at most 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_Refuse_When_ThereAreNoRows()
    {
        Assert.Contains(Read(Request()).Errors, e => e.Contains("at least one", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Should_Refuse_When_AnAmountIsNotPositive(decimal amount)
    {
        var request = Request(Row(amount)) with { ControlTotal = amount };

        Assert.Contains(Read(request).Errors, e => e.Contains("amount", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Should_Refuse_When_AnAmountHasMoreThanTwoDecimals()
    {
        var request = Request(Row(10.005m)) with { ControlTotal = 10.005m };

        Assert.Contains(Read(request).Errors, e => e.Contains("decimal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Should_Refuse_When_TheReferenceIsNotSafe()
    {
        // The reference becomes a batch id and part of a URL, so only safe characters (as job ids, T-101).
        var request = Request(Row()) with { Reference = "../etc/passwd" };

        Assert.Contains(Read(request).Errors, e => e.Contains("reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Should_HoldTheRow_When_TheAmountIsOverTheCap()
    {
        // FR-A-8: held, not refused, so one big line cannot fail a whole batch.
        var request = Request(Row(100m), Row(5000m, externalId: "row-2"));

        var result = Read(request);

        Assert.Empty(result.Errors);
        var held = Assert.Single(result.Held);
        Assert.Equal("row-2", held.ExternalId);
        Assert.Contains("amount-over-limit", held.Reason, StringComparison.Ordinal);
        Assert.Single(result.Lines);
    }

    [Theory]
    [InlineData("2020-01-01", "date-too-old")]
    [InlineData("2026-09-30", "date-in-the-future")]
    public void Should_HoldTheRow_When_TheDateIsOutsideTheWindow(string date, string reason)
    {
        var result = Read(Request(Row(date: date)));

        Assert.Empty(result.Errors);
        Assert.Contains(reason, Assert.Single(result.Held).Reason, StringComparison.Ordinal);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public void Should_AcceptTomorrow_When_TheFutureWindowIsOneDay()
    {
        var result = Read(Request(Row(date: Today.AddDays(1).ToString("yyyy-MM-dd"))));

        Assert.Empty(result.Held);
        Assert.Single(result.Lines);
    }

    [Fact]
    public void Should_HoldTheRow_When_TheAccountIsMissing()
    {
        var result = Read(Request(Row(account: "   ")));

        Assert.Contains("account-missing", Assert.Single(result.Held).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CheckTheControlTotalAgainstEveryRow_When_SomeRowsAreHeld()
    {
        // The control total covers what the caller submitted, not what survived: it is their arithmetic, checked as
        // given, so a held row must not make a correct total look wrong.
        var request = Request(Row(100m), Row(5000m, externalId: "row-2"));

        var result = Read(request);

        Assert.Empty(result.Errors);
        Assert.Equal(5100m, request.ControlTotal);
    }

    [Fact]
    public void Should_UseTheMemoAsDescription_When_OneIsGiven()
    {
        var line = Assert.Single(Read(Request(Row())).Lines).Line;

        Assert.Equal("INV 88213", line.Description);
        Assert.Equal("1042", line.CheckNo);
        Assert.Equal("4521", line.Last4);
    }

    /// <summary>
    /// T-915: <c>allowModelAccounts</c> was carried on the request, documented in <c>openapi.json</c> and given its own
    /// <c>qb:post:ai</c> scope — and read by nothing. A caller could set it, hold the scope, and watch every row a rule
    /// could not resolve be held as <c>unknown-account</c> with no hint that the model was never asked.
    /// <para>
    /// Refusing is the honest answer while Q-51 is unanswered. Implementing it unattended would mean a model choosing
    /// which account money lands in, which is exactly what <c>CLAUDE.md</c> rule 4 exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public void Should_RefuseTheBatch_When_TheCallerAsksForModelChosenAccounts()
    {
        var request = Request(Row()) with { AllowModelAccounts = true };

        var result = Read(request);

        var error = Assert.Single(result.Errors);
        Assert.Contains("allowModelAccounts", error, StringComparison.Ordinal);
        Assert.Contains("Q-51", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_ReadNothing_When_TheBatchIsRefusedForModelChosenAccounts()
    {
        // A refused request yields no lines at all: partial results invite partial posting.
        var result = Read(Request(Row()) with { AllowModelAccounts = true });

        Assert.Empty(result.Lines);
        Assert.Empty(result.Held);
    }

    [Fact]
    public void Should_ReadNormally_When_TheCallerLeavesModelAccountsAlone()
    {
        // The default must stay untouched: this is the path every existing caller uses.
        var result = Read(Request(Row()));

        Assert.Empty(result.Errors);
        Assert.Single(result.Lines);
    }
}
