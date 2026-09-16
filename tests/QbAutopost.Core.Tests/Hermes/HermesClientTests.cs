using System.Net;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Hermes;

public sealed class HermesClientTests : IDisposable
{
    private const string ApiKey = "sk-test-0123456789abcdef";
    private const string ValidJson = """{ "name": "Home Depot", "count": 2 }""";

    private readonly StubHttpHandler _handler = new();
    private readonly TempJobFolder _folder = new();

    private string AuditDir => _folder.PathOf("output", "hermes");

    public void Dispose()
    {
        _handler.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task Should_PostChatCompletionWithBearerModelAndZeroTemperature_When_Called()
    {
        _handler.ReplyContent(ValidJson);

        await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        var sent = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://hermes.test:8642/v1/chat/completions", sent.Uri.ToString());
        Assert.Equal("Bearer " + ApiKey, sent.Authorization);
        Assert.Equal("test-model", sent.Json.GetProperty("model").GetString());
        Assert.Equal(0m, sent.Json.GetProperty("temperature").GetDecimal());
        var messages = sent.Json.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("You extract facts.", sent.Message(0));
        Assert.Equal("the requirement", sent.Message(1));
    }

    [Fact]
    public async Task Should_AppendSchemaHintToSystemPrompt_When_Given()
    {
        _handler.ReplyContent(ValidJson);

        await Client().CompleteJsonAsync<Answer>(Request() with { SchemaHint = "{ \"name\": \"string\" }" }, CancellationToken.None);

        Assert.Equal("You extract facts.\n\n{ \"name\": \"string\" }", _handler.Requests[0].Message(0));
    }

    [Fact]
    public async Task Should_SendNoAuthorizationHeader_When_ApiKeyIsEmpty()
    {
        _handler.ReplyContent(ValidJson);

        await Client(apiKey: "").CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Null(_handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Should_ReturnAnswer_When_ReplyIsWrappedInCodeFence()
    {
        _handler.ReplyContent("```json\n" + ValidJson + "\n```");

        var answer = await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Equal(new Answer { Name = "Home Depot", Count = 2 }, answer);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Should_RetryWithValidationErrorsAppended_When_FirstAnswerFailsValidation()
    {
        _handler.ReplyContent("""{ "name": "Home Depot", "count": 0 }""").ReplyContent(ValidJson);

        var answer = await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Equal(2, answer.Count);
        Assert.Equal(2, _handler.Requests.Count);
        var retry = _handler.Requests[1].Message(1);
        Assert.StartsWith("the requirement\n\nYour previous reply was rejected:\n", retry, StringComparison.Ordinal);
        Assert.Contains("- count must be greater than 0", retry, StringComparison.Ordinal);
        Assert.Equal("You extract facts.", _handler.Requests[1].Message(0));
    }

    [Fact]
    public async Task Should_RetryWithParseError_When_FirstAnswerIsProse()
    {
        _handler.ReplyContent("Sure! Here is the data you asked for.").ReplyContent(ValidJson);

        var answer = await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Equal("Home Depot", answer.Name);
        Assert.Contains("not valid JSON", _handler.Requests[1].Message(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_RetryOnce_When_FirstReplyContentIsEmpty()
    {
        _handler.ReplyContent(null).ReplyContent(ValidJson);

        var answer = await Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None);

        Assert.Equal("Home Depot", answer.Name);
        Assert.Contains("empty", _handler.Requests[1].Message(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ThrowValidationExceptionAfterTwoCalls_When_BothAnswersAreInvalid()
    {
        _handler.ReplyContent("""{ "count": 1 }""").ReplyContent("""{ "count": 1 }""");

        var ex = await Assert.ThrowsAsync<HermesValidationException>(
            () => Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None));

        Assert.Equal(HermesTask.Spec, ex.Task);
        Assert.Contains("name is required", ex.Errors);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Should_ThrowUnavailableWithoutRetry_When_StatusIsNotSuccess()
    {
        _handler.ReplyRaw("""{ "error": "upstream down" }""", HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<HermesUnavailableException>(
            () => Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None));

        Assert.Equal("HTTP 502", ex.Reason);
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("""{ "choices": [] }""")]
    [InlineData("""{ "id": "x" }""")]
    public async Task Should_ThrowUnavailable_When_ResponseEnvelopeIsMalformed(string body)
    {
        _handler.ReplyRaw(body);

        await Assert.ThrowsAsync<HermesUnavailableException>(
            () => Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Should_ThrowUnavailable_When_ConnectionFails()
    {
        _handler.Throw(new HttpRequestException("Connection refused (hermes.test:8642)"));

        var ex = await Assert.ThrowsAsync<HermesUnavailableException>(
            () => Client().CompleteJsonAsync<Answer>(Request(), CancellationToken.None));

        Assert.Contains("Connection refused", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_ThrowUnavailable_When_TimeoutElapses()
    {
        _handler.Hang();

        var ex = await Assert.ThrowsAsync<HermesUnavailableException>(
            () => Client(timeout: TimeSpan.FromMilliseconds(50)).CompleteJsonAsync<Answer>(Request(), CancellationToken.None));

        Assert.StartsWith("timed out", ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_PropagateCancellation_When_CallerCancels()
    {
        _handler.Hang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client().CompleteJsonAsync<Answer>(Request(), cts.Token));
    }

    [Fact]
    public async Task Should_WriteNumberedAuditCopyPerAttempt_When_AuditDirIsGiven()
    {
        _handler.ReplyContent("not json").ReplyContent(ValidJson).ReplyContent(ValidJson);
        var client = Client();

        await client.CompleteJsonAsync<Answer>(Request() with { AuditDir = AuditDir }, CancellationToken.None);
        await client.CompleteJsonAsync<Answer>(Request() with { AuditDir = AuditDir }, CancellationToken.None);

        Assert.Equal(
            ["spec-1.request.json", "spec-1.response.json", "spec-2.request.json", "spec-2.response.json", "spec-3.request.json", "spec-3.response.json"],
            Directory.GetFiles(AuditDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(_handler.Requests[0].Body, File.ReadAllText(Path.Combine(AuditDir, "spec-1.request.json")));
        Assert.Contains("not json", File.ReadAllText(Path.Combine(AuditDir, "spec-1.response.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_WriteErrorResponseAuditCopy_When_CallTimesOut()
    {
        _handler.Hang();

        await Assert.ThrowsAsync<HermesUnavailableException>(() => Client(timeout: TimeSpan.FromMilliseconds(50))
            .CompleteJsonAsync<Answer>(Request() with { AuditDir = AuditDir }, CancellationToken.None));

        Assert.Contains("timed out", File.ReadAllText(Path.Combine(AuditDir, "spec-1.response.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_NeverWriteApiKey_When_WritingAuditCopies()
    {
        _handler.ReplyContent("echo " + ApiKey).ReplyContent(ValidJson);

        await Client().CompleteJsonAsync<Answer>(
            Request() with { AuditDir = AuditDir, UserContent = "key is " + ApiKey }, CancellationToken.None);

        var files = Directory.GetFiles(AuditDir);
        Assert.Equal(4, files.Length);
        Assert.All(files, file => Assert.DoesNotContain(ApiKey, File.ReadAllText(file), StringComparison.Ordinal));
    }

    [Fact]
    public void Should_NotShowApiKey_When_OptionsAreFormatted()
    {
        Assert.DoesNotContain(ApiKey, Options(ApiKey, TimeSpan.FromSeconds(1)).ToString(), StringComparison.Ordinal);
    }

    private static HermesRequest Request() => new(HermesTask.Spec, "You extract facts.", "the requirement");

    private static HermesOptions Options(string apiKey, TimeSpan timeout) => new()
    {
        BaseUrl = "http://hermes.test:8642/",
        ApiKey = apiKey,
        Model = "test-model",
        Timeout = timeout,
    };

    private HermesClient Client(string apiKey = ApiKey, TimeSpan? timeout = null) =>
        new(
            new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            Options(apiKey, timeout ?? TimeSpan.FromSeconds(30)));

    private sealed record Answer : IValidatable
    {
        public string? Name { get; init; }

        public int Count { get; init; }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Name))
            {
                errors.Add("name is required");
            }

            if (Count <= 0)
            {
                errors.Add("count must be greater than 0");
            }

            return errors;
        }
    }
}
