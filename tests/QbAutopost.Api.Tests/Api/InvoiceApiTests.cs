using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;

namespace QbAutopost.Api.Tests.Api;

/// <summary>Invoices through the host (spec FR-5): T3 (fake Hermes) → matching → <c>invoices/&lt;file&gt;.json</c> and unmatched reporting.</summary>
public sealed class InvoiceApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public InvoiceApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_MatchSampleInvoiceAndReportNothingUnmatched_When_SampleJobRuns()
    {
        var folder = _factory.Dir.CopySampleJob();

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Empty(view.UnmatchedInvoices);
        Assert.Empty(view.Unreadable);
        var invoice = Assert.Single(_factory.Hermes.Calls, c => c.Task == HermesTask.Invoice);
        Assert.Equal(Path.Combine(folder, "output", "hermes"), invoice.AuditDir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "output", "invoices", "home-depot-88213.pdf.json")));
        Assert.Equal(16, doc.RootElement.GetProperty("facts").GetProperty("matchedRequestId").GetString()!.Length);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "output", "result.json")));
        Assert.Equal(0, result.RootElement.GetProperty("unmatchedInvoices").GetArrayLength());
    }

    [Fact]
    public async Task Should_ReportUnmatchedInvoiceInViewAndResult_When_NoLineHasItsTotal()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(r => r.Task == HermesTask.Invoice
            ? FakeHermesClient.FixtureJson(HermesTask.Invoice).Replace("184.32", "999.99", StringComparison.Ordinal)
            : null);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal((8, 2, 1), (view.Counts.ToPost, view.Counts.Held, view.Counts.Skipped));
        var unmatched = Assert.Single(view.UnmatchedInvoices);
        Assert.Equal("home-depot-88213.pdf", unmatched.File);
        Assert.Equal(HoldReasons.NoMatchingLine, unmatched.Reason);
        Assert.Equal(999.99m, unmatched.Facts!.Total);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "output", "result.json")));
        var item = Assert.Single(result.RootElement.GetProperty("unmatchedInvoices").EnumerateArray());
        Assert.Equal("no-matching-line", item.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Should_StayReadyAndReportInvoice_When_HermesCannotReadIt()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(r => r.Task == HermesTask.Invoice
            ? throw new HermesUnavailableException(HermesTask.Invoice, "HTTP 503")
            : null);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal(8, view.Counts.ToPost);
        var unmatched = Assert.Single(view.UnmatchedInvoices);
        Assert.Equal(HoldReasons.HermesFailed, unmatched.Reason);
        Assert.Null(unmatched.Facts);
        Assert.Contains(unmatched.Errors, e => e.Contains("HTTP 503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_ReportImageInvoiceUnreadable_When_OcrIsDisabled()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.WriteAllBytes(Path.Combine(folder, "invoices", "receipt.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        var unmatched = Assert.Single(view.UnmatchedInvoices);
        Assert.Equal("receipt.jpg", unmatched.File);
        Assert.Equal(HoldReasons.Unreadable, unmatched.Reason);
        Assert.Single(_factory.Hermes.Calls, c => c.Task == HermesTask.Invoice);
    }

    [Fact]
    public async Task Should_ReportAmbiguousAndHoldNothingExtra_When_TwoInvoicesClaimTheSameLine()
    {
        var folder = _factory.Dir.CopySampleJob();
        File.Copy(
            Path.Combine(folder, "invoices", "home-depot-88213.pdf"),
            Path.Combine(folder, "invoices", "home-depot-copy.pdf"));

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal((8, 2, 1), (view.Counts.ToPost, view.Counts.Held, view.Counts.Skipped));
        Assert.Equal(["home-depot-88213.pdf", "home-depot-copy.pdf"], view.UnmatchedInvoices.Select(i => i.File));
        Assert.All(view.UnmatchedInvoices, i => Assert.Equal(HoldReasons.Ambiguous, i.Reason));
    }
}
