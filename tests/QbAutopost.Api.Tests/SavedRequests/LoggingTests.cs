using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1015 (saved-requests spec 12): every line written while a saved-request route runs carries <c>savedRequestId</c>,
/// and during a post also <c>attemptId</c> and, once QuickBooks has answered, <c>postId</c>. The lines are captured
/// from Serilog's own pipeline with the log context enabled, as the real host has it.
/// </summary>
public sealed class LoggingTests : IDisposable
{
    private readonly SavedRequestTestHost _host = new();
    private readonly CapturingSink _sink = new();

    public void Dispose() => _host.Dispose();

    private HttpClient Client(string role)
    {
        var factory = _host.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var logger = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(_sink).CreateLogger();
            services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(logger, dispose: true));
        }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.Factory.Users.TokenFor(role));
        return client;
    }

    private async Task<long> SaveAsync(string fixture)
    {
        var (response, body) = await _host.SaveAsync(SyncPostFixtures.Read(fixture));
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        return body.GetProperty("id").GetInt64();
    }

    private static bool IsOurs(LogEvent e) =>
        e.Properties.TryGetValue("SourceContext", out var context) && context.ToString().Contains("QbAutopost", StringComparison.Ordinal);

    [Fact]
    public async Task Should_CarrySavedRequestIdOnEveryLine_When_Posting()
    {
        var id = await SaveAsync(SyncPostFixtures.Posted);
        using var editor = Client(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "post");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SavedRequestTestHost.BodyOf(response);
        var postId = body.GetProperty("postId").GetString();
        var attemptId = body.GetProperty("attemptId").GetInt64();
        var lines = _sink.Events.Where(e => IsOurs(e) && SourceOf(e).EndsWith("SavedRequestPoster", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, e => Assert.Equal(id.ToString(), Value(e, "savedRequestId")));
        Assert.Contains(lines, e => Value(e, "attemptId") == attemptId.ToString());
        // The recording line is written after QuickBooks answered, so it names the post too.
        Assert.Contains(lines, e => Value(e, "postId") == $"\"{postId}\"" && e.RenderMessage().Contains("recorded as"));
    }

    [Fact]
    public async Task Should_CarrySavedRequestIdOnEngineLines_When_Posting()
    {
        var id = await SaveAsync(SyncPostFixtures.Posted);
        using var editor = Client(DashboardRoles.Editor);

        using var response = await SavedRequestTestHost.PostActionAsync(editor, id, "post");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var engine = _sink.Events.Where(e => IsOurs(e) && !SourceOf(e).EndsWith("SavedRequestPoster", StringComparison.Ordinal)).ToList();
        var carried = engine.Where(e => Value(e, "savedRequestId") == id.ToString()).ToList();
        Assert.NotEmpty(carried);
    }

    [Fact]
    public async Task Should_CarrySavedRequestIdOnTheRouteLines_When_ALedgerBatchCannotBeRead()
    {
        var id = await SaveAsync(SyncPostFixtures.Partial);
        _host.Factory.Gateway.RejectLine = 2;
        using var editor = Client(DashboardRoles.Editor);
        using var post = await SavedRequestTestHost.PostActionAsync(editor, id, "post");
        var posted = await SavedRequestTestHost.BodyOf(post);
        var attemptId = posted.GetProperty("attemptId").GetInt64();
        var postId = posted.GetProperty("postId").GetString()!;
        Directory.Delete(_host.Factory.Dir.Combine("data", "api-batches", postId), recursive: true);
        _sink.Clear();

        using var response = await editor.GetAsync($"{SavedRequestTestHost.Route}/{id}/attempts/{attemptId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var warning = Assert.Single(_sink.Events, e => IsOurs(e) && e.Level == LogEventLevel.Warning);
        Assert.Equal(id.ToString(), Value(warning, "savedRequestId"));
        Assert.Equal(attemptId.ToString(), Value(warning, "attemptId"));
    }

    private static string SourceOf(LogEvent e) => e.Properties["SourceContext"].ToString().Trim('"');

    private static string? Value(LogEvent e, string name) => e.Properties.TryGetValue(name, out var v) ? v.ToString() : null;

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly object _gate = new();
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToList();
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _events.Clear();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_gate)
            {
                _events.Add(logEvent);
            }
        }
    }
}
