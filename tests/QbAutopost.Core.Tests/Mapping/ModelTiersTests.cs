using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

/// <summary>Spec FR-6 tiers 3 (invoice hint → T4) and 4 (T4 alone) with gate G3 (FR-7), for lines tiers 1–2 left open.</summary>
public sealed class ModelTiersTests
{
    private const double Threshold = 0.8;
    private const string Plumber = "Joe's Plumbing";
    private const string Repairs = "Repairs and Maintenance";

    private static readonly QbLists Lists = new()
    {
        Vendors = [Plumber],
        Accounts =
        [
            new QbAccount { Name = "Chase Checking 4521", Type = "Bank" },
            new QbAccount { Name = Repairs, Type = "Expense" },
            new QbAccount { Name = "Utilities", Type = "Expense" },
            new QbAccount { Name = "Office Supplies", Type = "Expense" },
        ],
    };

    private static readonly StatementLine PlumberLine = Lines.Bank(Direction.Debit, 3199.70m, "ACH DEBIT JOES PLUMBING", date: "2026-08-15");

    private readonly ScriptedHermes _hermes;

    private string _answer = Answer(Repairs, 0.82);

    public ModelTiersTests() => _hermes = new ScriptedHermes(_ => _answer);

    [Fact]
    public async Task Should_PostInvoiceTier_When_HintedAnswerReachesThreshold()
    {
        var txn = await ResolveOne(invoices: [Invoice("plumbing parts")]);

        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Null(txn.Reason);
        Assert.Equal(Confidence.Invoice, txn.Confidence);
        Assert.Equal(3, txn.Tier);
        Assert.Equal(Repairs, txn.LineAccount);
        Assert.Equal(0.82, txn.ModelConfidence);
        Assert.Equal([Repairs, "Utilities"], txn.Candidates);
        Assert.Contains("invoice hint: plumbing parts", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_PostInvoiceTierWithoutHistory_When_ScoreEqualsThreshold()
    {
        _answer = Answer(Repairs, 0.8);

        var txn = await ResolveOne(invoices: [Invoice("plumbing parts")], history: []);

        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal(Confidence.Invoice, txn.Confidence);
    }

    [Fact]
    public async Task Should_HoldLowConfidenceAsModelTier_When_HintedAnswerIsBelowThreshold()
    {
        _answer = Answer(Repairs, 0.79);

        var txn = await ResolveOne(invoices: [Invoice("plumbing parts")], history: [Posted(Plumber, Repairs)]);

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.LowConfidence, txn.Reason);
        Assert.Equal(Confidence.Hold, txn.Confidence);
        Assert.Equal(4, txn.Tier);
        Assert.Null(txn.LineAccount);
        Assert.Equal([Repairs, "Utilities"], txn.Candidates);
        Assert.Single(_hermes.Requests);
    }

    [Fact]
    public async Task Should_PostModelTier_When_ScoreReachesThresholdAndPayeeWasPostedToTheAccount()
    {
        var txn = await ResolveOne(history: [Posted(Plumber, Repairs)]);

        Assert.Equal(Decision.Post, txn.Decision);
        Assert.Equal(Confidence.Model, txn.Confidence);
        Assert.Equal(4, txn.Tier);
        Assert.Equal(Repairs, txn.LineAccount);
        Assert.Contains("invoice hint: none", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_HoldNoPriorPosting_When_ModelIsConfidentButPayeeWasNeverPostedThere()
    {
        _answer = Answer(Repairs, 0.99);

        var txn = await ResolveOne(history: [Posted(Plumber, "Utilities")]);

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.NoPriorPosting, txn.Reason);
        Assert.Equal(4, txn.Tier);
        Assert.Null(txn.LineAccount);
        Assert.Equal(Repairs, txn.Candidates[0]);
        Assert.Equal(0.99, txn.ModelConfidence);
    }

    [Fact]
    public async Task Should_HoldLowConfidence_When_ModelScoreIsBelowThreshold()
    {
        _answer = Answer(Repairs, 0.5);

        var txn = await ResolveOne(history: [Posted(Plumber, Repairs)]);

        Assert.Equal(HoldReasons.LowConfidence, txn.Reason);
    }

    /// <summary>
    /// FR-7 matrix for lines that reach the model. Prior = the ledger has the payee posted to the chosen account.
    /// Expected: decision, confidence, tier, reason (null when posted).
    /// </summary>
    [Theory]
    // invoice hint (tier 3): threshold alone decides
    [InlineData(true, 0.80, false, "post", "invoice", 3, null)]
    [InlineData(true, 1.00, true, "post", "invoice", 3, null)]
    [InlineData(true, 0.79, true, "hold", "hold", 4, "low-confidence")]
    [InlineData(true, 0.00, false, "hold", "hold", 4, "low-confidence")]
    // no hint (tier 4): threshold and a prior posting
    [InlineData(false, 0.80, true, "post", "model", 4, null)]
    [InlineData(false, 1.00, true, "post", "model", 4, null)]
    [InlineData(false, 0.80, false, "hold", "hold", 4, "no-prior-posting")]
    [InlineData(false, 1.00, false, "hold", "hold", 4, "no-prior-posting")]
    [InlineData(false, 0.79, true, "hold", "hold", 4, "low-confidence")]
    [InlineData(false, 0.79, false, "hold", "hold", 4, "low-confidence")]
    public async Task Should_FollowTheG3Matrix_When_ModelAnswers(
        bool hint, double score, bool prior, string decision, string confidence, int tier, string? reason)
    {
        _answer = Answer(Repairs, score);

        var txn = await ResolveOne(
            invoices: hint ? [Invoice("plumbing parts")] : [],
            history: prior ? [Posted(Plumber, Repairs)] : [Posted(Plumber, "Utilities")]);

        Assert.Equal(decision, txn.Decision.ToString(), ignoreCase: true);
        Assert.Equal(confidence, txn.Confidence.ToString(), ignoreCase: true);
        Assert.Equal(tier, txn.Tier);
        Assert.Equal(reason, txn.Reason);
        Assert.Equal(score, txn.ModelConfidence);
        Assert.Equal(decision == "post" ? Repairs : null, txn.LineAccount);
        Assert.Single(_hermes.Requests);
    }

    [Theory]
    [InlineData(0.5, 0.5, "post")]
    [InlineData(0.5, 0.49, "hold")]
    [InlineData(1.0, 0.99, "hold")]
    [InlineData(1.0, 1.0, "post")]
    public async Task Should_UseTheGivenThreshold_When_DecidingTheInvoiceTier(double threshold, double score, string decision)
    {
        _answer = Answer(Repairs, score);

        var resolved = await new ModelTiers(TestAccountChooser.Create(_hermes)).ResolveAsync(
            [Held()],
            new ModelTierInput(threshold, Lists, [], [Invoice("plumbing parts")], "audit"),
            CancellationToken.None);

        Assert.Equal(decision, Assert.Single(resolved).Decision.ToString(), ignoreCase: true);
    }

    [Fact]
    public async Task Should_NoteTheModelChoice_When_LineIsHeld()
    {
        _answer = """{ "account": "Repairs and Maintenance", "confidence": 0.5, "reason": "Plumbing service." }""";

        var txn = await ResolveOne();

        Assert.Contains("Repairs and Maintenance", txn.Note, StringComparison.Ordinal);
        Assert.Contains("0.50", txn.Note, StringComparison.Ordinal);
        Assert.Contains("Plumbing service.", txn.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_UseModelTier_When_MatchedInvoiceIsFromACustomer()
    {
        var customerInvoice = Invoice("plumbing parts") with { Role = PartyRole.Customer };

        var txn = await ResolveOne(invoices: [customerInvoice], history: []);

        Assert.Equal(HoldReasons.NoPriorPosting, txn.Reason);
        Assert.Contains("invoice hint: none", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Should_UseModelTier_When_MatchedInvoiceHasNoHint(string? hint)
    {
        var txn = await ResolveOne(invoices: [Invoice(hint)], history: []);

        Assert.Equal(4, txn.Tier);
        Assert.Contains("invoice hint: none", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_IgnoreInvoices_When_TwoAreMatchedToTheSameLine()
    {
        var txn = await ResolveOne(invoices: [Invoice("plumbing parts"), Invoice("pipes") with { File = "b.pdf" }], history: []);

        Assert.Equal(4, txn.Tier);
    }

    [Fact]
    public async Task Should_HoldHermesFailed_When_ChooserFails()
    {
        _answer = Answer("Plumbing", 0.9);

        var txn = await ResolveOne(history: [Posted(Plumber, "Plumbing")]);

        Assert.Equal(Decision.Hold, txn.Decision);
        Assert.Equal(HoldReasons.HermesFailed, txn.Reason);
        Assert.Contains("Plumbing", txn.Note, StringComparison.Ordinal);
        Assert.Null(txn.Tier);
        Assert.Null(txn.LineAccount);
    }

    [Fact]
    public async Task Should_HoldNoAccounts_When_ListsHaveNoAccounts()
    {
        var txn = await ResolveOne(lists: Lists with { Accounts = [] });

        Assert.Equal(HoldReasons.NoAccounts, txn.Reason);
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_LeaveOtherLinesAlone_When_TheyDoNotNeedTheModel()
    {
        var mapper = new Mapper(Fixtures.SampleRules(), Lists);
        var mapped = mapper.MapAll(
        [
            Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA"),
            Lines.Bank(Direction.Debit, 55m, "ACH DEBIT SOMEBODY UNKNOWN", lineNo: 2),
            Lines.Bank(Direction.Debit, 420m, "CHECK 1043", checkNo: "1043", lineNo: 3),
        ]);

        var resolved = await Resolve(mapped);

        Assert.Equal(mapped, resolved);
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_KeepLineOrder_When_SomeLinesAreResolved()
    {
        var mapper = new Mapper(Fixtures.SampleRules(), Lists);
        var mapped = mapper.MapAll(
        [
            Lines.Bank(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA"),
            PlumberLine with { LineNo = 2 },
        ]);

        var resolved = await Resolve(mapped, history: [Posted(Plumber, Repairs)]);

        Assert.Equal(mapped.Select(m => m.RequestId), resolved.Select(m => m.RequestId));
        Assert.Equal(mapped[0], resolved[0]);
        Assert.Equal(Decision.Post, resolved[1].Decision);
    }

    [Fact]
    public async Task Should_KeepPayeeNote_When_LineIsResolved()
    {
        var held = Held() with { Note = "payee from invoice a.pdf" };

        var txn = Assert.Single(await Resolve([held], history: [Posted(Plumber, Repairs)]));

        Assert.StartsWith("payee from invoice a.pdf; ", txn.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_SendAuditFolder_When_CallingHermes()
    {
        await ResolveOne();

        var request = Assert.Single(_hermes.Requests);
        Assert.Equal(HermesTask.Account, request.Task);
        Assert.Equal("audit", request.AuditDir);
    }

    private static MappedTxn Held()
    {
        var txn = new Mapper(Fixtures.SampleRules(), Lists).Map(PlumberLine);
        Assert.Equal(HoldReasons.NoAccountRule, txn.Reason);
        Assert.Equal(Plumber, txn.Payee);
        return txn;
    }

    private async Task<MappedTxn> ResolveOne(
        IReadOnlyList<InvoiceFacts>? invoices = null, IReadOnlyList<LedgerEntry>? history = null, QbLists? lists = null) =>
        Assert.Single(await Resolve([Held()], invoices, history, lists));

    private Task<IReadOnlyList<MappedTxn>> Resolve(
        IReadOnlyList<MappedTxn> mapped,
        IReadOnlyList<InvoiceFacts>? invoices = null,
        IReadOnlyList<LedgerEntry>? history = null,
        QbLists? lists = null) =>
        new ModelTiers(TestAccountChooser.Create(_hermes)).ResolveAsync(
            mapped,
            new ModelTierInput(Threshold, lists ?? Lists, history ?? [], invoices ?? [], "audit"),
            CancellationToken.None);

    private static string Answer(string account, double confidence) =>
        $$"""{ "account": "{{account}}", "confidence": {{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "alternatives": ["Utilities"] }""";

    private static InvoiceFacts Invoice(string? hint) => new()
    {
        File = "a.pdf",
        Party = Plumber,
        Role = PartyRole.Vendor,
        Date = new DateOnly(2026, 8, 14),
        Total = 3199.70m,
        CategoryHint = hint,
        MatchedRequestId = PlumberLine.RequestId,
    };

    private static LedgerEntry Posted(string payee, string lineAccount) => new()
    {
        BatchId = "old#1",
        JobId = "old",
        Fingerprint = "fp-old",
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
