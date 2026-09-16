using System.Net;
using QbAutopost.Api.QuickBooks;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Tests.Api;

/// <summary>FR-8 gate G4 against QuickBooks while posting, through the host and the fake company.</summary>
public sealed class DuplicateCheckApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;
    private string _folder = "";

    public DuplicateCheckApiTests() => _client = _factory.CreateAuthorizedClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private string Output(string file) => Path.Combine(_folder, "output", file);

    private static SimulatedTxn Fpl(string date, string? payee) => new()
    {
        Type = "Check",
        TxnId = "EXISTING-1",
        Account = "Chase Checking 4521",
        Payee = payee,
        Date = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture),
        RefNumber = "ACH",
        Amount = 311.40m,
    };

    private async Task<Endpoints.JobView> ReadyThenPost()
    {
        _folder = _factory.Dir.CopySampleJob();
        Assert.Equal(JobStatus.Ready, (await _client.RunToEndAsync(_folder)).Status);
        using var response = await _client.PostAsync("/jobs/2026-08-tropicana/post", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await _client.WaitForJobAsync("2026-08-tropicana");
    }

    [Fact]
    public async Task Should_SendNoQuery_When_JobIsADryRun()
    {
        var folder = _factory.Dir.CopySampleJob();

        await _client.RunToEndAsync(folder);

        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_QueryEachKindAndAccountBeforeAdding_When_Posting()
    {
        await ReadyThenPost();

        // Checks and deposits on 4521, card charges and credits on 7788.
        Assert.Equal(4, _factory.Gateway.Queries.Count);
        Assert.Equal(5, _factory.Gateway.Requests.Count);
        Assert.Same(_factory.Gateway.Writes.Single(), _factory.Gateway.Requests[^1]);
    }

    [Fact]
    public async Task Should_WriteQueryAuditFiles_When_Posting()
    {
        await ReadyThenPost();

        foreach (var n in Enumerable.Range(1, 4))
        {
            Assert.True(File.Exists(Output($"query-{n}.qbxml")));
            Assert.True(File.Exists(Output($"query-{n}.response.qbxml")));
        }
    }

    [Fact]
    public async Task Should_SkipAlreadyInQuickBooksAndNotSendIt_When_QuickBooksHasTheSameLine()
    {
        _factory.Gateway.Company.AddExisting(Fpl("2026-08-12", "Florida Power & Light"));

        var view = await ReadyThenPost();

        Assert.Equal(7, view.Counts.Posted);
        var skipped = Assert.Single(view.Skipped, s => s.Reason == HoldReasons.AlreadyInQuickBooks);
        Assert.Equal("QuickBooks TxnID EXISTING-1", skipped.Note);
        Assert.DoesNotContain(skipped.RequestId, _factory.Gateway.Writes.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain(skipped.RequestId, File.ReadAllText(Output("request.qbxml")), StringComparison.Ordinal);
        Assert.DoesNotContain(new LedgerStore(_factory.LedgerFile).Load().Posted, p => p.Fingerprint.StartsWith(skipped.RequestId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HoldPossibleDuplicate_When_QuickBooksHasSameAmountNearby()
    {
        _factory.Gateway.Company.AddExisting(Fpl("2026-08-14", payee: null));

        var view = await ReadyThenPost();

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.Equal(7, view.Counts.Posted);
        var held = Assert.Single(view.Held, h => h.Reason == HoldReasons.PossibleDuplicate);
        Assert.Contains("EXISTING-1", held.Note, StringComparison.Ordinal);
        Assert.Contains(HoldReasons.PossibleDuplicate, File.ReadAllText(Output("analysis.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_PostNothingAndRecordNothing_When_DuplicateQueryCannotReachQuickBooks()
    {
        _factory.Gateway.QueryThrow = new QuickBooksUnavailableException("no session");

        var view = await ReadyThenPost();

        Assert.Equal(JobStatus.Partial, view.Status);
        Assert.StartsWith("nothing posted: duplicate check (G4) failed", view.Error, StringComparison.Ordinal);
        Assert.Empty(_factory.Gateway.Writes);
        Assert.Empty(new LedgerStore(_factory.LedgerFile).Load().Jobs);
    }
}
