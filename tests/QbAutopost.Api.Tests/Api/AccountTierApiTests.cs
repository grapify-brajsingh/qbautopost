using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// Tiers 3–4 and G3 through the host (spec FR-6, FR-7, §10): the sample plumber line gets a payee from an invoice
/// (Joe's Plumbing, a synced vendor without an account rule) and its account from the fake Hermes T4.
/// </summary>
public sealed class AccountTierApiTests : IDisposable
{
    private const string PlumberInvoice = """
        { "party": "Joe's Plumbing", "role": "vendor", "number": "77", "date": "2026-08-14", "total": 3199.70, "categoryHint": "plumbing repair" }
        """;

    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public AccountTierApiTests()
    {
        new QbListsStore(_factory.Dir.Combine("data", "qb-lists.json")).Save(new QbLists
        {
            Vendors = ["Joe's Plumbing"],
            Accounts =
            [
                new QbAccount { Name = "Chase Checking 4521", Type = "Bank" },
                new QbAccount { Name = "Repairs and Maintenance", Type = "Expense" },
                new QbAccount { Name = "Office Supplies", Type = "Expense" },
                new QbAccount { Name = "Utilities", Type = "Expense" },
            ],
        });
        _factory.Hermes.Respond(r => r.Task == HermesTask.Invoice ? PlumberInvoice : null);
        _client = _factory.CreateAuthorizedClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Should_PostPlumberAtInvoiceTier_When_T4AnswerReachesThreshold()
    {
        var folder = _factory.Dir.CopySampleJob();

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal((9, 1, 1), (view.Counts.ToPost, view.Counts.Held, view.Counts.Skipped));
        var call = Assert.Single(_factory.Hermes.Calls, c => c.Task == HermesTask.Account);
        Assert.Contains("invoice hint: plumbing repair", call.UserContent, StringComparison.Ordinal);
        var line = PlumberLine(folder);
        Assert.Equal(3, line.GetProperty("tier").GetInt32());
        Assert.Equal("invoice", line.GetProperty("confidence").GetString());
        Assert.Equal(0.82, line.GetProperty("modelConfidence").GetDouble());
        Assert.Equal("Repairs and Maintenance", line.GetProperty("lineAccount").GetString());
        Assert.Equal("post", line.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Should_ShowHeldPlumberWithAccountCandidates_When_G3HoldsTheModelAnswer()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(r => r.Task == HermesTask.Account
            ? """{ "account": "Repairs and Maintenance", "confidence": 0.6, "alternatives": ["Utilities"] }"""
            : null);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        Assert.Equal(8, view.Counts.ToPost);
        var held = Assert.Single(view.Held, h => h.Amount == 3199.70m);
        Assert.Equal(HoldReasons.LowConfidence, held.Reason);
        Assert.Equal(["Repairs and Maintenance", "Utilities"], held.Candidates);
        var line = PlumberLine(folder);
        Assert.Equal(4, line.GetProperty("tier").GetInt32());
        Assert.Equal("hold", line.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Should_HoldPlumberHermesFailed_When_T4NamesAnUnlistedAccount()
    {
        var folder = _factory.Dir.CopySampleJob();
        _factory.Hermes.Respond(r => r.Task == HermesTask.Account
            ? """{ "account": "Plumbing", "confidence": 0.99 }"""
            : null);

        var view = await _client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        var held = Assert.Single(view.Held, h => h.Amount == 3199.70m);
        Assert.Equal(HoldReasons.HermesFailed, held.Reason);
        Assert.DoesNotContain("Plumbing", File.ReadAllText(Path.Combine(folder, "output", "request.qbxml")), StringComparison.Ordinal);
    }

    private static JsonElement PlumberLine(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "output", "analysis.json")));
        return doc.RootElement.GetProperty("lines").EnumerateArray()
            .Single(l => l.GetProperty("amount").GetDecimal() == 3199.70m)
            .Clone();
    }
}
