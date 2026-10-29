using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// T-1212b: OpenRouter faked at the <see cref="HttpMessageHandler"/> level (dashboard-api-m12 anti-goal 6), so the real
/// <c>HermesClient</c> builds and sends the request and the test can read exactly what would have left the server.
/// Replies are queued; a request with nothing queued fails the test loudly.
/// </summary>
public sealed class FakeOpenRouterHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _replies = new();
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    /// <summary>One request as it was sent: URL, headers and body.</summary>
    public sealed record CapturedRequest(Uri? Uri, string? Authorization, string Body)
    {
        public JsonObject Json => (JsonObject)JsonNode.Parse(Body)!;
    }

    public IReadOnlyList<CapturedRequest> Requests => _requests.ToList();

    /// <summary>Set when a request's cancellation token fired while the handler waited.</summary>
    public bool SawCancellation { get; private set; }

    /// <summary>Completes when the next request arrives (set before sending).</summary>
    public TaskCompletionSource Arrived { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A chat completion whose <c>choices[0].message.content</c> is <paramref name="content"/>.</summary>
    public FakeOpenRouterHandler ReplyContent(string content) =>
        Enqueue((_, _) => Task.FromResult(Ok(content)));

    /// <summary>The model's content is the text of <c>tests/fixtures/extractions/<paramref name="name"/></c>.</summary>
    public FakeOpenRouterHandler ReplyFixture(string name) =>
        ReplyContent(File.ReadAllText(Fixtures.PathOf("extractions", name), Encoding.UTF8));

    public FakeOpenRouterHandler ReplyStatus(HttpStatusCode status, string body = """{"error":{"message":"upstream failed"}}""") =>
        Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));

    /// <summary>
    /// A 200 chat completion whose content is <paramref name="content"/> built from the bearer token the request carried,
    /// as a misbehaving provider might echo it back (a key-scrub test must be able to fail).
    /// </summary>
    public FakeOpenRouterHandler ReplyContentEchoingKey(Func<string, string> content) =>
        Enqueue((request, _) => Task.FromResult(Ok(content(BearerOf(request)))));

    /// <summary>A non-2xx reply whose body is <paramref name="body"/> built from the bearer token the request carried.</summary>
    public FakeOpenRouterHandler ReplyStatusEchoingKey(HttpStatusCode status, Func<string, string> body) =>
        Enqueue((request, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body(BearerOf(request)), Encoding.UTF8, "application/json"),
        }));

    private static string BearerOf(HttpRequestMessage request) => request.Headers.Authorization?.Parameter ?? "";

    /// <summary>Never answers: waits until the request is cancelled (a timeout or a disconnect).</summary>
    public FakeOpenRouterHandler ReplyNever() =>
        Enqueue(async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }

            throw new InvalidOperationException("unreachable");
        });

    /// <summary>Answers with <paramref name="content"/> once <paramref name="release"/> completes.</summary>
    public FakeOpenRouterHandler ReplyAfter(Task release, string content) =>
        Enqueue(async (_, ct) =>
        {
            await release.WaitAsync(ct);
            return Ok(content);
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new CapturedRequest(request.RequestUri, request.Headers.Authorization?.ToString(), body));
        var arrived = Arrived;
        Arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        arrived.TrySetResult();

        if (!_replies.TryDequeue(out var reply))
        {
            throw new InvalidOperationException("FakeOpenRouterHandler: a request arrived with no reply queued.");
        }

        return await reply(request, cancellationToken);
    }

    private FakeOpenRouterHandler Enqueue(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    private static HttpResponseMessage Ok(string content)
    {
        var envelope = new JsonObject
        {
            ["id"] = "gen-test",
            ["model"] = "test-model",
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                ["finish_reason"] = "stop",
            }),
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}
