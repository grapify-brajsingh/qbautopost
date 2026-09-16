using System.Net;
using System.Text;
using System.Text.Json;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>
/// An in-memory HTTP server for client tests: answers queued responses in order and records every request.
/// Never opens a socket (CLAUDE.md rule 1).
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Queues an OpenAI-style completion whose <c>choices[0].message.content</c> is <paramref name="content"/>.</summary>
    public StubHttpHandler ReplyContent(string? content) =>
        ReplyRaw(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }));

    public StubHttpHandler ReplyRaw(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    /// <summary>Queues a response that never arrives before the request is cancelled.</summary>
    public StubHttpHandler Hang()
    {
        _responses.Enqueue(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    public StubHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            body));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No stub response queued.");
        }

        return await _responses.Dequeue()(cancellationToken);
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;

        /// <summary><c>messages[index].content</c> of the chat request.</summary>
        public string Message(int index) => Json.GetProperty("messages")[index].GetProperty("content").GetString()!;
    }
}
