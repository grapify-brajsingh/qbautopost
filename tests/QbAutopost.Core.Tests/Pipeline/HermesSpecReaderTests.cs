using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

public sealed class HermesSpecReaderTests : IDisposable
{
    private static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Spec);

    private readonly TempJobFolder _folder = TempJobFolder.FromSample();
    private readonly JobInput _input;

    public HermesSpecReaderTests() => _input = FolderReader.Read(_folder.Folder);

    private string Requirement => File.ReadAllText(_input.RequirementPath);

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Should_ReturnHermesSpec_When_AnswerIsValid()
    {
        var hermes = new ScriptedHermes(_ => Fixtures.Read("hermes", "spec.json"));

        var result = await new HermesSpecReader(hermes, Prompts).ReadAsync(_input, CancellationToken.None);

        Assert.Equal(SpecSources.Hermes, result.Source);
        Assert.Null(result.Note);
        Assert.Equal([TxnKind.Check, TxnKind.CcCharge, TxnKind.CcCredit, TxnKind.Deposit], result.Spec.Kinds);
        Assert.Equal(["4521"], result.Spec.BankLast4);
        Assert.Equal(["7788"], result.Spec.CardLast4);
    }

    [Fact]
    public async Task Should_SendSpecTaskWithRenderedPromptAndRequirement_When_Reading()
    {
        var hermes = new ScriptedHermes(_ => Fixtures.Read("hermes", "spec.json"));

        await new HermesSpecReader(hermes, Prompts).ReadAsync(_input, CancellationToken.None);

        var request = Assert.Single(hermes.Requests);
        Assert.Equal(HermesTask.Spec, request.Task);
        Assert.Equal(Requirement, request.UserContent);
        Assert.DoesNotContain("{{", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Check, CreditCard, Deposit", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_input.OutputDir, "hermes"), request.AuditDir);
    }

    [Fact]
    public async Task Should_FallBackToRegexWithNote_When_AnswerFailsValidationTwice()
    {
        var hermes = new ScriptedHermes(_ => throw new HermesValidationException(HermesTask.Spec, ["kinds contains \"Wire\""]));

        var result = await new HermesSpecReader(hermes, Prompts).ReadAsync(_input, CancellationToken.None);

        Assert.Equal(SpecSources.RegexFallback, result.Source);
        Assert.Contains("kinds contains \"Wire\"", result.Note, StringComparison.Ordinal);
        var regex = RegexSpecParser.Parse(Requirement);
        Assert.Equal(regex.Kinds, result.Spec.Kinds);
        Assert.Equal(regex.BankLast4, result.Spec.BankLast4);
        Assert.Equal(regex.CardLast4, result.Spec.CardLast4);
    }

    [Fact]
    public async Task Should_Propagate_When_HermesIsUnavailable()
    {
        var hermes = new ScriptedHermes(_ => throw new HermesUnavailableException(HermesTask.Spec, "HTTP 503"));

        await Assert.ThrowsAsync<HermesUnavailableException>(
            () => new HermesSpecReader(hermes, Prompts).ReadAsync(_input, CancellationToken.None));
    }

    [Fact]
    public async Task Should_FallBackAndKeepAuditCopies_When_RealClientGetsTwoMalformedReplies()
    {
        using var handler = new StubHttpHandler().ReplyContent("I think the kinds are checks.").ReplyContent("```json\n{ \"kinds\": [\"Wire\"] }\n```");
        using var http = new HttpClient(handler);
        var client = new HermesClient(http, new HermesOptions { BaseUrl = "http://hermes.test", ApiKey = "sk-audit-test-key-123" });

        var result = await new HermesSpecReader(client, Prompts).ReadAsync(_input, CancellationToken.None);

        Assert.Equal(SpecSources.RegexFallback, result.Source);
        Assert.Equal(2, handler.Requests.Count);
        var audit = Path.Combine(_input.OutputDir, "hermes");
        Assert.Equal(
            ["spec-1.request.json", "spec-1.response.json", "spec-2.request.json", "spec-2.response.json"],
            Directory.GetFiles(audit).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(Directory.GetFiles(audit), f =>
            Assert.DoesNotContain("sk-audit-test-key-123", File.ReadAllText(f), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ReturnHermesSpec_When_RealClientSucceedsOnRetry()
    {
        using var handler = new StubHttpHandler()
            .ReplyContent("""{ "kinds": ["Check"], "bankLast4": ["45 21"], "cardLast4": [] }""")
            .ReplyContent(Fixtures.Read("hermes", "spec.json"));
        using var http = new HttpClient(handler);
        var client = new HermesClient(http, new HermesOptions { BaseUrl = "http://hermes.test" });

        var result = await new HermesSpecReader(client, Prompts).ReadAsync(_input, CancellationToken.None);

        Assert.Equal(SpecSources.Hermes, result.Source);
        Assert.Contains("bankLast4 contains \"45 21\"", handler.Requests[1].Message(1), StringComparison.Ordinal);
    }

    /// <summary>Answers every call with the JSON the script returns (or the exception it throws); validates like the real client.</summary>
    private sealed class ScriptedHermes(Func<HermesRequest, string> script) : IHermesClient
    {
        public List<HermesRequest> Requests { get; } = [];

        public Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
            where T : IValidatable
        {
            Requests.Add(request);
            var answer = JsonReply.TryParse<T>(script(request), out var errors);
            return answer is not null
                ? Task.FromResult(answer)
                : Task.FromException<T>(new HermesValidationException(request.Task, errors));
        }
    }
}
