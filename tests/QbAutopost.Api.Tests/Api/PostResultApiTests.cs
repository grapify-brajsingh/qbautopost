using System.Net;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>FR-12 / §13 after a post: G5, ledger contents, result.json, posted vs partial.</summary>
public sealed class PostResultApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private HttpClient _client;
    private string _folder = "";

    public PostResultApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>Sample rules plus names for the two lines the sample holds, so every line can post.</summary>
    private void UseRulesThatResolveEverything()
    {
        var rules = File.ReadAllText(Fixtures.SampleRules)
            .Replace("\"FPL\": \"Florida Power & Light\"", "\"FPL\": \"Florida Power & Light\", \"UNKNOWN PLUMBER\": \"Unknown Plumber LLC\"", StringComparison.Ordinal)
            .Replace("\"PALM COURT\": \"Palm Court Rentals LLC\"", "\"PALM COURT\": \"Palm Court Rentals LLC\", \"SUNRISE PROPERTY\": \"Sunrise Property Mgmt\"", StringComparison.Ordinal)
            .Replace("\"Florida Power & Light\": \"Utilities\"", "\"Florida Power & Light\": \"Utilities\", \"Unknown Plumber LLC\": \"Repairs and Maintenance\"", StringComparison.Ordinal);
        var path = _factory.Dir.Combine("rules-all.json");
        File.WriteAllText(path, rules);
        _client.Dispose();
        _client = _factory.WithSetting("Company:RulesFile", path).CreateClient();
        _client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
    }

    private async Task<Endpoints.JobView> ReadyThenPost()
    {
        _folder = _factory.Dir.CopySampleJob();
        Assert.Equal(JobStatus.Ready, (await _client.RunToEndAsync(_folder)).Status);
        using var response = await _client.PostAsync("/jobs/2026-08-tropicana/post", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await _client.WaitForJobAsync("2026-08-tropicana");
    }

    [Fact]
    public async Task Should_EndPosted_When_NothingIsHeld()
    {
        UseRulesThatResolveEverything();

        var view = await ReadyThenPost();

        Assert.Equal(JobStatus.Posted, view.Status);
        Assert.Equal((10, 0, 1), (view.Counts.Posted, view.Counts.Held, view.Counts.Skipped));
        Assert.Null(view.Error);
    }

    [Fact]
    public async Task Should_RecordPostedLineFieldsInLedger_When_Posted()
    {
        var view = await ReadyThenPost();

        var fpl = Assert.Single(new LedgerStore(_factory.LedgerFile).Load().Posted, p => p.Payee == "Florida Power & Light");
        var posted = Assert.Single(view.Posted, p => p.Payee == "Florida Power & Light");
        Assert.Equal(posted.TxnId, fpl.TxnId);
        Assert.StartsWith(posted.RequestId, fpl.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(
            ("2026-08-tropicana#1", "2026-08-tropicana", "1", TxnKind.Check, "Chase Checking 4521", "Utilities", 311.40m),
            (fpl.BatchId, fpl.JobId, fpl.EditSequence, fpl.Kind, fpl.Account, fpl.LineAccount, fpl.Amount));
        Assert.Equal(
            (new DateOnly(2026, 8, 12), "ACH", "ACH DEBIT FPL ELECTRIC UTILITY", "chase-checking-4521.csv", false),
            (fpl.Date, fpl.RefNumber, fpl.Memo, fpl.SourceFile, fpl.Undone));
        Assert.True(fpl.LineNo > 0);
    }

    [Fact]
    public async Task Should_WriteResultWithPostedItems_When_Posted()
    {
        var view = await ReadyThenPost();

        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "output", "result.json")));
        var root = result.RootElement;
        Assert.Equal("partial", root.GetProperty("status").GetString());
        Assert.Equal("2026-08-tropicana#1", root.GetProperty("batchId").GetString());
        Assert.True(root.GetProperty("dryRun").GetBoolean()); // the job was submitted as a dry run, then posted
        Assert.Equal(8, root.GetProperty("posted").GetArrayLength());
        Assert.Equal(
            view.Posted.Select(p => p.TxnId).Order(),
            root.GetProperty("posted").EnumerateArray().Select(p => p.GetProperty("txnId").GetString()!).Order());
        Assert.Equal(view.Totals.ToPost, root.GetProperty("totals").GetProperty("posted").GetDecimal());
    }

    [Fact]
    public async Task Should_RecordDepositFromDepositTotalEcho_When_Posted()
    {
        await ReadyThenPost();

        var deposit = Assert.Single(new LedgerStore(_factory.LedgerFile).Load().Posted, p => p.Kind == TxnKind.Deposit);
        Assert.Equal(("Palm Court Rentals LLC", "Rental Income", 2400.00m), (deposit.Payee, deposit.LineAccount, deposit.Amount));
    }
}
