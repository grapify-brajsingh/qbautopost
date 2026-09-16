using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

/// <summary>Hermes T4 (spec §9.4): one line → one account from the allowed list, with up to 3 candidates.</summary>
public sealed class AccountChooserTests
{
    private const string AuditDir = "audit-dir";

    private static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Account);

    private static readonly IReadOnlyList<string> Accounts = ["Office Supplies", "Repairs and Maintenance", "Utilities", "Travel"];

    private static readonly AccountQuestion HomeDepot = new(
        Lines.Card(Direction.Debit, 184.32m, "HOME DEPOT #4521 NOIDA", date: "2026-08-22"),
        TxnKind.CcCharge,
        "Home Depot",
        "plumbing fittings, repair");

    private readonly ScriptedHermes _hermes = new(_ => Fixtures.Read("hermes", "account.json"));

    [Fact]
    public void Should_KeepOnlyExpenseIncomeAndCogsTypes_When_TypesAreKnown()
    {
        var lists = Lists(
            ("Checking", "Bank"), ("Repairs and Maintenance", "Expense"), ("Rental Income", "Income"),
            ("Materials", "CostOfGoodsSold"), ("Penalties", "OtherExpense"), ("Interest Earned", "OtherIncome"),
            ("Chase Card", "CreditCard"), ("Owner Equity", "Equity"));

        Assert.Equal(
            ["Repairs and Maintenance", "Rental Income", "Materials", "Penalties"],
            AccountChooser.ChoosableAccounts(lists));
    }

    [Fact]
    public void Should_MatchTypesIgnoringCase_When_ListUsesOtherCasing()
    {
        var lists = Lists(("Utilities", "expense"), ("Materials", "COSTOFGOODSSOLD"), ("Checking", "bank"));

        Assert.Equal(["Utilities", "Materials"], AccountChooser.ChoosableAccounts(lists));
    }

    [Fact]
    public void Should_KeepEveryAccount_When_NoTypeIsKnown()
    {
        var lists = Lists(("Checking", null), ("Utilities", null));

        Assert.Equal(["Checking", "Utilities"], AccountChooser.ChoosableAccounts(lists));
    }

    [Fact]
    public void Should_DropUntypedAccounts_When_SomeTypesAreKnown()
    {
        var lists = Lists(("Utilities", "Expense"), ("Mystery", null));

        Assert.Equal(["Utilities"], AccountChooser.ChoosableAccounts(lists));
    }

    [Fact]
    public void Should_DropBlankAndRepeatedNames_When_ListingAccounts()
    {
        var lists = Lists(("Utilities", "Expense"), (" ", "Expense"), ("Utilities", "Expense"), ("utilities", "Expense"));

        Assert.Equal(["Utilities", "utilities"], AccountChooser.ChoosableAccounts(lists));
    }

    [Fact]
    public async Task Should_ReturnTheChosenAccount_When_AnswerIsValid()
    {
        var result = await Choose();

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        var choice = result.Choice!;
        Assert.Equal("Repairs and Maintenance", choice.Account);
        Assert.Equal(0.82, choice.Confidence);
        Assert.StartsWith("Invoice lists plumbing fittings", choice.Reason, StringComparison.Ordinal);
        Assert.Equal(["Repairs and Maintenance", "Office Supplies", "Utilities"], choice.Candidates);
    }

    [Fact]
    public async Task Should_SendAccountTaskWithLinePayeeHintAndAccounts_When_Asked()
    {
        await Choose();

        var request = Assert.Single(_hermes.Requests);
        Assert.Equal(HermesTask.Account, request.Task);
        Assert.Equal(AuditDir, request.AuditDir);
        Assert.NotNull(request.Check);
        Assert.DoesNotContain("{{", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(
            """
            Transaction:
            date: 2026-08-22
            description: HOME DEPOT #4521 NOIDA
            amount: 184.32
            direction: debit
            kind: ccCharge
            payee: Home Depot
            invoice hint: plumbing fittings, repair

            Accounts (one per line):
            Office Supplies
            Repairs and Maintenance
            Utilities
            Travel
            """.ReplaceLineEndings("\n"),
            request.UserContent);
    }

    [Fact]
    public async Task Should_SayNone_When_ThereIsNoInvoiceHint()
    {
        await Choose(HomeDepot with { InvoiceHint = null });

        Assert.Contains("\ninvoice hint: none\n", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_WriteAmountInvariant_When_AmountHasTrailingZeros()
    {
        await Choose(HomeDepot with { Line = HomeDepot.Line with { Amount = 1250m } });

        Assert.Contains("\namount: 1250.00\n", Assert.Single(_hermes.Requests).UserContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Repairs")]
    [InlineData("repairs and maintenance")]
    public async Task Should_HoldHermesFailed_When_AccountIsNotExactlyListed(string account)
    {
        var hermes = new ScriptedHermes(_ => $$"""{ "account": "{{account}}", "confidence": 0.95 }""");

        var result = await Choose(hermes: hermes);

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Null(result.Choice);
        Assert.Contains(result.Errors, e => e.Contains(account, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HoldHermesFailed_When_ConfidenceIsOutOfRange()
    {
        var hermes = new ScriptedHermes(_ => """{ "account": "Utilities", "confidence": 1.5 }""");

        var result = await Choose(hermes: hermes);

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
    }

    [Fact]
    public async Task Should_HoldHermesFailed_When_HermesIsUnreachable()
    {
        var hermes = new ScriptedHermes(_ => throw new HermesUnavailableException(HermesTask.Account, "connection refused"));

        var result = await Choose(hermes: hermes);

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("connection refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HoldNoAccounts_When_ListIsEmpty()
    {
        var result = await Choose(accounts: []);

        Assert.Equal(HoldReasons.NoAccounts, result.HoldReason);
        Assert.Empty(_hermes.Requests);
    }

    [Fact]
    public async Task Should_KeepOnlyListedDistinctCandidates_When_AlternativesAreMessy()
    {
        var hermes = new ScriptedHermes(_ => """
            { "account": "Utilities", "confidence": 0.4,
              "alternatives": ["Utilities", "Gifts", null, "", "travel", "Travel", "Office Supplies", "Repairs and Maintenance"] }
            """);

        var result = await Choose(hermes: hermes);

        Assert.Equal(["Utilities", "Travel", "Office Supplies"], result.Choice!.Candidates);
    }

    [Fact]
    public async Task Should_ReturnOnlyTheAccountAsCandidate_When_ThereAreNoAlternatives()
    {
        var hermes = new ScriptedHermes(_ => """{ "account": "Travel", "confidence": 0.9, "reason": null }""");

        var result = await Choose(hermes: hermes);

        Assert.Equal(["Travel"], result.Choice!.Candidates);
        Assert.Null(result.Choice.Reason);
    }

    [Fact]
    public async Task Should_PropagateCancellation_When_TokenIsCancelled()
    {
        var hermes = new ScriptedHermes(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Choose(hermes: hermes));
    }

    [Fact]
    public async Task Should_ReturnChoice_When_RealClientSucceedsOnRetry()
    {
        using var handler = new StubHttpHandler()
            .ReplyContent("""{ "account": "Repairs & Maintenance", "confidence": 0.82 }""")
            .ReplyContent("```json\n" + Fixtures.Read("hermes", "account.json") + "\n```");
        using var http = new HttpClient(handler);
        var client = new HermesClient(http, new HermesOptions { BaseUrl = "http://hermes.test" });

        var result = await new AccountChooser(client, Prompts).ChooseAsync(HomeDepot, Accounts, null, CancellationToken.None);

        Assert.Equal("Repairs and Maintenance", result.Choice!.Account);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"Repairs & Maintenance\"", handler.Requests[1].Message(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_HoldHermesFailed_When_RealClientGetsTwoUnlistedAccounts()
    {
        using var handler = new StubHttpHandler()
            .ReplyContent("""{ "account": "Repairs", "confidence": 0.82 }""")
            .ReplyContent("""{ "account": "Repairs", "confidence": 0.82 }""");
        using var http = new HttpClient(handler);
        var client = new HermesClient(http, new HermesOptions { BaseUrl = "http://hermes.test" });

        var result = await new AccountChooser(client, Prompts).ChooseAsync(HomeDepot, Accounts, null, CancellationToken.None);

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Equal(2, handler.Requests.Count);
    }

    private Task<AccountChoiceResult> Choose(
        AccountQuestion? question = null, ScriptedHermes? hermes = null, IReadOnlyList<string>? accounts = null) =>
        new AccountChooser(hermes ?? _hermes, Prompts)
            .ChooseAsync(question ?? HomeDepot, accounts ?? Accounts, AuditDir, CancellationToken.None);

    private static QbLists Lists(params (string Name, string? Type)[] accounts) => new()
    {
        Accounts = accounts.Select(a => new QbAccount { Name = a.Name, Type = a.Type }).ToList(),
    };
}
