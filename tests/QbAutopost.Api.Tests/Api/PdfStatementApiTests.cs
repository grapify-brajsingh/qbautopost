using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;

namespace QbAutopost.Api.Tests.Api;

/// <summary>A PDF bank statement through the host: PdfText → Hermes T2 (fake) → G1 → mapping (spec FR-3/FR-4).</summary>
public sealed class PdfStatementApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public PdfStatementApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_ReachReadyWithSameCounts_When_BankStatementIsATextPdf()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.Delete(Path.Combine(folder, "statements", "chase-checking-4521.csv"));
        File.Copy(Fixtures.PathOf("statements", "chase-checking-4521.pdf"), Path.Combine(folder, "statements", "chase-checking-4521.pdf"));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal((8, 2, 1), (view.Counts.ToPost, view.Counts.Held, view.Counts.Skipped));
        Assert.Equal([HermesTask.Spec, HermesTask.Statement, HermesTask.Invoice], _factory.Hermes.Calls.Select(c => c.Task));
        Assert.Equal(Path.Combine(folder, "output", "hermes"), _factory.Hermes.Calls[1].AuditDir);
        Assert.True(File.Exists(Path.Combine(folder, "output", "statements", "chase-checking-4521.pdf.rows.json")));
    }

    [Fact]
    public async Task Should_HoldPdfAndStayReady_When_HermesCannotReadTheStatement()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.Delete(Path.Combine(folder, "statements", "chase-checking-4521.csv"));
        File.Copy(Fixtures.PathOf("statements", "chase-checking-4521.pdf"), Path.Combine(folder, "statements", "chase-checking-4521.pdf"));
        _factory.Hermes.Respond(r => r.Task == HermesTask.Statement
            ? throw new HermesUnavailableException(HermesTask.Statement, "HTTP 503")
            : null);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        var pdf = Assert.Single(view.Statements, s => s.File.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal("hermes-failed", pdf.HoldReason);
        Assert.Equal(3, view.Counts.ToPost);
    }
}
