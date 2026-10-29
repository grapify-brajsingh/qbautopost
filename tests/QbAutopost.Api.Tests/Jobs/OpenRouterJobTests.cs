using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Output;

namespace QbAutopost.Api.Tests.Jobs;

/// <summary>
/// T-1214: the job pipeline (T1 spec, T2 statement, T3 invoice) through the real <see cref="HermesClient"/> with
/// OpenRouter faked at the handler (no network, rule 1), against the same job through <see cref="FakeHermesClient"/>.
/// </summary>
public sealed class OpenRouterJobTests : IDisposable
{
    private const string Key = "sk-or-v1-JOBMARKER-7731-do-not-leak";

    private readonly ApiFactory _fake = new();
    private readonly ApiFactory _real = new();
    private readonly FakeOpenRouterHandler _handler = new();

    public void Dispose()
    {
        _fake.Dispose();
        _real.Dispose();
    }

    private static string PdfJob(ApiFactory factory)
    {
        var folder = factory.Dir.CopySampleJob();
        File.Delete(Path.Combine(folder, "statements", "chase-checking-4521.csv"));
        File.Copy(Fixtures.PathOf("statements", "chase-checking-4521.pdf"), Path.Combine(folder, "statements", "chase-checking-4521.pdf"));
        return folder;
    }

    private HttpClient RealClient(IDictionary<string, string?>? settings = null, Serilog.Core.ILogEventSink? logs = null)
    {
        var all = new Dictionary<string, string?>
        {
            // ApiFactory points the base at an unresolvable host; the shipped default is what this test is about.
            ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
            ["OpenRouter:ApiKey"] = Key,
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>())
        {
            all[k] = v;
        }

        var host = _real.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(all));
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHermesClient>();
                // As Program wires it: the real client over the fake handler, or the disabled one when OpenRouter:Enabled is false.
                services.AddSingleton<IHermesClient>(sp => new LoggingHermesClient(
                    string.Equals(sp.GetRequiredService<IConfiguration>()["OpenRouter:Enabled"], "false", StringComparison.OrdinalIgnoreCase)
                        ? new DisabledHermesClient()
                        : new HermesClient(new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan }, sp.GetRequiredService<HermesOptions>()),
                    sp.GetRequiredService<ILogger<LoggingHermesClient>>()));
                if (logs is not null)
                {
                    // Every event, before any scrubbing, as ExtractionTestHost captures them.
                    var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(logs).CreateLogger();
                    services.AddSingleton<ILoggerFactory>(new Serilog.Extensions.Logging.SerilogLoggerFactory(logger, dispose: true));
                }
            });
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    private void QueueAnswers() => _handler
        .ReplyContent(FakeHermesClient.FixtureJson(HermesTask.Spec))
        .ReplyContent(FakeHermesClient.FixtureJson(HermesTask.Statement))
        .ReplyContent(FakeHermesClient.FixtureJson(HermesTask.Invoice));

    private static string Output(string folder, params string[] parts) =>
        Path.Combine([folder, FolderReader.OutputDirName, .. parts]);

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    /// <summary>Drops what legitimately differs between two runs: timestamps (startedUtc, finishedUtc, ...) and durations (any property named so).</summary>
    private static JsonNode Normalise(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Select(p => p.Key).ToList())
                {
                    if (name.EndsWith("Utc", StringComparison.Ordinal) || name.EndsWith("At", StringComparison.Ordinal) || name.EndsWith("Ms", StringComparison.Ordinal)
                        || name.Equals("elapsed", StringComparison.OrdinalIgnoreCase))
                    {
                        o.Remove(name);
                    }
                    else if (o[name] is { } child)
                    {
                        Normalise(child);
                    }
                }

                break;
            case JsonArray a:
                foreach (var item in a.Where(i => i is not null))
                {
                    Normalise(item!);
                }

                break;
        }

        return node;
    }

    [Fact]
    public async Task Should_RunPdfJobThroughOpenRouter_When_Configured()
    {
        var fakeFolder = PdfJob(_fake);
        var fakeView = await _fake.CreateAuthorizedClient().RunToEndAsync(fakeFolder);
        QueueAnswers();
        var realFolder = PdfJob(_real);

        using var client = RealClient();
        var realView = await client.RunToEndAsync(realFolder);

        Assert.Equal(JobStatus.Ready, realView.Status);
        Assert.Equal(fakeView.Status, realView.Status);
        Assert.Equal(3, _handler.Requests.Count);
        Assert.All(_handler.Requests, r =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", r.Uri?.ToString());
            Assert.Equal("Bearer " + Key, r.Authorization);
            Assert.Equal("~google/gemini-pro-latest", (string?)r.Json["model"]);
        });

        var rowsPath = Path.Combine("statements", "chase-checking-4521.pdf.rows.json");
        Assert.Equal(
            Normalise(Read(Output(fakeFolder, rowsPath))).ToJsonString(),
            Normalise(Read(Output(realFolder, rowsPath))).ToJsonString());
        Assert.Equal(
            Normalise(Read(Output(fakeFolder, JobOutputWriter.ResultFile))).ToJsonString(),
            Normalise(Read(Output(realFolder, JobOutputWriter.ResultFile))).ToJsonString());
    }

    [Fact]
    public async Task Should_HoldAsToday_When_OpenRouterDisabled()
    {
        var folder = PdfJob(_real);
        using var client = RealClient(new Dictionary<string, string?> { ["OpenRouter:Enabled"] = "false" });

        var view = await client.RunToEndAsync(folder);

        Assert.Empty(_handler.Requests);
        Assert.Equal(JobStatus.Ready, view.Status);
        var pdf = Assert.Single(view.Statements, s => s.File.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal("hermes-failed", pdf.HoldReason);
    }

    [Fact]
    public async Task Should_ScrubKeyFromAuditCopies_When_JobRuns()
    {
        // The provider echoes the key back: in a 200 answer (T1, as an extra property the reader ignores) and in an
        // error body (T2). Neither may reach the audit copies or the log.
        _handler
            .ReplyContentEchoingKey(key =>
            {
                var spec = JsonNode.Parse(FakeHermesClient.FixtureJson(HermesTask.Spec))!.AsObject();
                spec["echo"] = "you sent " + key;
                return spec.ToJsonString();
            })
            .ReplyStatusEchoingKey(System.Net.HttpStatusCode.Unauthorized, key => "{\"error\":{\"message\":\"bad key " + key + "\"}}")
            .ReplyContent(FakeHermesClient.FixtureJson(HermesTask.Invoice));
        var logs = new SavedRequests.ExtractionTestHost.CapturingSink();
        var folder = PdfJob(_real);
        using var client = RealClient(logs: logs);

        await client.RunToEndAsync(folder);

        Assert.Equal(3, _handler.Requests.Count);
        var audit = Directory.GetFiles(Output(folder, "hermes"));
        Assert.NotEmpty(audit);
        var texts = audit.Select(File.ReadAllText).ToList();
        var logText = logs.Text;
        Assert.NotEmpty(logs.Events);
        foreach (var fragment in Fragments(Key, 8))
        {
            Assert.All(texts, t => Assert.DoesNotContain(fragment, t, StringComparison.Ordinal));
            Assert.DoesNotContain(fragment, logText, StringComparison.Ordinal);
        }

        // The echoes did arrive (so the checks above had something to catch) and were scrubbed in place.
        Assert.Contains(texts, t => t.Contains("you sent ***", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("bad key ***", StringComparison.Ordinal));
    }

    /// <summary>Every substring of <paramref name="key"/> of length <paramref name="length"/> (longer ones contain one).</summary>
    private static IEnumerable<string> Fragments(string key, int length) =>
        Enumerable.Range(0, key.Length - length + 1).Select(i => key.Substring(i, length));
}
