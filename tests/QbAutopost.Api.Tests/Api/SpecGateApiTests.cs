using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Api;

/// <summary>Requirement → spec through Hermes T1, gate G2 and the regex fallback (spec FR-2, plan T-203).</summary>
public sealed class SpecGateApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public SpecGateApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_WriteSpecJsonMatchingGolden_When_SampleJobIsReadByHermes()
    {
        var folder = _factory.Dir.CopySampleJob();

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal([HermesTask.Spec, HermesTask.Invoice], _factory.Hermes.Calls.Select(c => c.Task));
        var actual = File.ReadAllText(Path.Combine(folder, "output", "spec.json")).ReplaceLineEndings("\n");
        var goldenPath = Fixtures.PathOf("output", "sample-spec.golden.json");
        var expected = File.Exists(goldenPath) ? File.ReadAllText(goldenPath).ReplaceLineEndings("\n") : "";
        if (actual != expected)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sample-spec.actual.json"), actual);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Should_FailWithExplanationInSpecJson_When_HermesStatesAccountWithoutStatement()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(_ => Spec(bank: """["4521", "9999"]"""));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.StartsWith("requirement check failed (G2)", view.Error, StringComparison.Ordinal);
        Assert.Contains("account 9999 is stated in the requirement but has no statement file", view.Error, StringComparison.Ordinal);
        var spec = ReadSpecJson(folder);
        Assert.Equal("hermes", spec.GetProperty("source").GetString());
        Assert.False(spec.GetProperty("gate").GetProperty("ok").GetBoolean());
        Assert.Contains(
            spec.GetProperty("gate").GetProperty("errors").EnumerateArray(),
            e => e.GetString()!.Contains("9999", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(folder, "output", "analysis.json")));
        Assert.False(File.Exists(Path.Combine(folder, "output", "request.qbxml")));
    }

    [Fact]
    public async Task Should_Fail_When_HermesFindsNoKinds()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(_ => Spec(kinds: "[]"));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Contains("no transaction kinds found", view.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Fail_When_StatementAccountIsNeitherStatedNorRegistered()
    {
        var rules = _factory.Dir.Combine("rules-without-card.json");
        File.WriteAllText(rules, File.ReadAllText(Fixtures.SampleRules)
            .Replace("\"CardAccounts\": { \"7788\": \"Chase Sapphire 7788\" }", "\"CardAccounts\": {}", StringComparison.Ordinal));
        Assert.DoesNotContain("\"7788\":", File.ReadAllText(rules), StringComparison.Ordinal);
        using var factory = _factory.WithSetting("Company:RulesFile", rules);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(_ => Spec(kinds: """["Check", "Deposit"]""", card: "[]"));

        var view = await client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Contains("(account 7788) is neither stated in the requirement nor registered in rules.json", view.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_Fail_When_HermesNamesAnotherCompany()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(_ => Spec(company: "\"Palm Grove Holdings\""));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Contains("requirement names company 'Palm Grove Holdings'", view.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_UseConfiguredCompany_When_HermesReturnsPlaceholder()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(_ => Spec(company: "\"Company\""));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        var spec = ReadSpecJson(folder);
        Assert.Equal(ApiFactory.Company, spec.GetProperty("company").GetString());
        Assert.Equal(JsonValueKind.Null, spec.GetProperty("spec").GetProperty("company").ValueKind);
    }

    [Fact]
    public async Task Should_FallBackToRegexAndReachReady_When_HermesAnswerFailsValidation()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(_ => Spec(kinds: """["Check", "Wire"]"""));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        var spec = ReadSpecJson(folder);
        Assert.Equal("regex-fallback", spec.GetProperty("source").GetString());
        Assert.Contains("kinds contains \"Wire\"", spec.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            ["check", "ccCharge", "ccCredit", "deposit"],
            spec.GetProperty("spec").GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task Should_FailOnFallbackReading_When_HermesIsInvalidAndRegexFindsNothing()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.WriteAllText(Path.Combine(folder, "requirement.txt"), "please post august");
        _factory.Hermes.Respond(_ => """{ "kinds": ["Everything"], "bankLast4": [], "cardLast4": [] }""");

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Contains("no transaction kinds found", view.Error, StringComparison.Ordinal);
        Assert.Equal("regex-fallback", ReadSpecJson(folder).GetProperty("source").GetString());
    }

    [Fact]
    public async Task Should_FailJobWithoutFallback_When_HermesIsUnavailable()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(r => throw new HermesUnavailableException(r.Task, "HTTP 503"));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Failed, view.Status);
        Assert.Contains("Hermes Spec call failed: HTTP 503", view.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "output", "request.qbxml")));
    }

    private static JsonElement ReadSpecJson(string folder) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "output", "spec.json"))).RootElement;

    private static string Spec(
        string company = "\"Tropicana Properties LLC\"",
        string kinds = """["Check", "CreditCard", "Deposit"]""",
        string bank = """["4521"]""",
        string card = """["7788"]""") =>
        $$"""{ "company": {{company}}, "kinds": {{kinds}}, "bankLast4": {{bank}}, "cardLast4": {{card}} }""";
}
