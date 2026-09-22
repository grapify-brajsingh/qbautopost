using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// T-907 / FR-A-6: <c>POST /api/v1/quickbooks/transactions/validate</c>. The offline dry run — it answers with
/// QuickBooks shut down, because names come from <c>qb-lists.json</c> and duplicates from <c>ledger.json</c>.
/// 200 when the batch is clean, 422 with the <b>same body</b> when it is not: a validator that explains itself only
/// on success is useless.
/// </summary>
public sealed class TransactionValidateApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>Resolves through the sample rules: alias HOME DEPOT → Home Depot → Repairs and Maintenance (tier 1).</summary>
    private static object Row(
        string externalId = "row-1",
        decimal amount = 184.32m,
        string kind = "Check",
        string account = "Chase Checking 4521",
        string? lineAccount = null,
        string? payee = "HOME DEPOT #6412",
        string? memo = null,
        string last4 = "4521",
        string date = "2026-08-05") =>
        new
        {
            externalId,
            kind,
            date,
            amount,
            account,
            lineAccount,
            payee,
            memo = memo ?? "HOME DEPOT #6412 PURCHASE",
            last4,
        };

    private static object Request(params object[] rows) => new
    {
        reference = "payroll-2026-09",
        controlTotal = rows.Sum(r => (decimal)r.GetType().GetProperty("amount")!.GetValue(r)!),
        transactions = rows,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(object request, string query = "")
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/quickbooks/transactions/validate" + query, request, ApiFactory.Json);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static JsonElement Row(JsonElement body, string externalId) =>
        body.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("externalId").GetString() == externalId);

    [Fact]
    public async Task Should_Answer200_When_EveryRowWouldPost()
    {
        var (status, body) = await PostAsync(Request(Row()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("submitted").GetInt32());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("wouldPost").GetInt32());
        Assert.Equal(0, body.GetProperty("counts").GetProperty("held").GetInt32());

        var row = Row(body, "row-1");
        Assert.Equal("wouldPost", row.GetProperty("outcome").GetString());
        Assert.Equal("Repairs and Maintenance", row.GetProperty("lineAccount").GetString());
        Assert.Equal("Home Depot", row.GetProperty("payee").GetString());
        Assert.Equal(16, row.GetProperty("requestId").GetString()!.Length);
    }

    [Fact]
    public async Task Should_NeverCallQuickBooksOrHermes_When_ARequestIsValidated()
    {
        // The whole point of FR-A-6: a caller can validate before the QuickBooks server is up. Asserted against the
        // recording fakes rather than throwing ones, so an attempted-then-swallowed call would also fail this.
        var (status, _) = await PostAsync(Request(Row()));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(_factory.Gateway.Requests);
        Assert.Empty(_factory.Hermes.Calls);
    }

    [Fact]
    public async Task Should_Answer422WithTheSameBody_When_ARowIsHeld()
    {
        var unresolved = Row(externalId: "row-2", payee: "NEVER HEARD OF THEM", memo: "NEVER HEARD OF THEM LLC");

        var (status, body) = await PostAsync(Request(Row(), unresolved));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("wouldPost").GetInt32());
        Assert.Equal(1, body.GetProperty("counts").GetProperty("held").GetInt32());
        // The same shape as the 200: rows, counts and totals are all still there.
        Assert.Equal("held", Row(body, "row-2").GetProperty("outcome").GetString());
        Assert.Equal("no-account-rule", Row(body, "row-2").GetProperty("reason").GetString());
        Assert.Equal(184.32m, body.GetProperty("totals").GetProperty("wouldPost").GetDecimal());
    }

    [Fact]
    public async Task Should_Answer400_When_TheControlTotalDoesNotMatch()
    {
        var request = new { reference = "payroll-2026-09", controlTotal = 1.00m, transactions = new[] { Row() } };

        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/quickbooks/transactions/validate", request, ApiFactory.Json);

        // §6.1: the rows and the caller's arithmetic disagree, so the request itself is unusable — not a gate verdict.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Contains("control-total-mismatch", problem.GetProperty("detail").GetString());
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_Answer400_When_NoTransactionsAreSent()
    {
        var (status, _) = await PostAsync(new { reference = "empty", transactions = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Should_ReportADuplicate_When_TheLedgerAlreadyHasTheFingerprint()
    {
        // Computed from the spec §7 formula, not read back from the API: the ledger entry stands for a line posted
        // earlier from a statement, and G4 must recognise it across both entrances.
        var fingerprint = Fingerprints.Compute(
            SourceKind.Bank, "4521", new DateOnly(2026, 8, 5), 184.32m, Direction.Debit, null, "HOME DEPOT #6412 PURCHASE");
        new LedgerStore(_factory.LedgerFile).Save(new Ledger
        {
            Posted =
            [
                new LedgerEntry
                {
                    BatchId = "2026-08-tropicana#1", JobId = "2026-08-tropicana", Fingerprint = fingerprint,
                    TxnId = "8A1-1234", Kind = TxnKind.Check, Account = "Chase Checking 4521",
                    Amount = 184.32m, Date = new DateOnly(2026, 8, 5), SourceFile = "statement.csv",
                },
            ],
        });

        var (status, body) = await PostAsync(Request(Row()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(1, body.GetProperty("counts").GetProperty("duplicates").GetInt32());
        Assert.Equal(Fingerprints.ToRequestId(fingerprint), Row(body, "row-1").GetProperty("requestId").GetString());
        var row = Row(body, "row-1");
        Assert.Equal("duplicate", row.GetProperty("outcome").GetString());
        Assert.Equal("2026-08-tropicana#1", row.GetProperty("batchId").GetString());
    }

    [Fact]
    public async Task Should_ReportUnknownNames_When_TheListsDoNotHaveThem()
    {
        new QbListsStore(_factory.Dir.Combine("data", "qb-lists.json")).Save(new QbLists
        {
            SyncedUtc = DateTime.UtcNow,
            Accounts = [new QbAccount { Name = "Chase Checking 4521" }],
            Vendors = ["Home Depot"],
        });

        var (status, body) = await PostAsync(Request(Row(lineAccount: "Repairs & Maint.")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("unknown-account", Row(body, "row-1").GetProperty("reason").GetString());
        Assert.Contains(
            "Repairs & Maint.",
            body.GetProperty("unknownNames").GetProperty("accounts").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task Should_CountTheQbXmlRequests_When_TheBatchWouldPost()
    {
        var (_, body) = await PostAsync(Request(Row()));

        var qbXml = body.GetProperty("qbXml");
        Assert.Equal(1, qbXml.GetProperty("requestCount").GetInt32());
        Assert.Equal(1, qbXml.GetProperty("byType").GetProperty("CheckAdd").GetInt32());
        // Counts only: the request body is never returned by default, and never logged (spec §14).
        Assert.False(qbXml.TryGetProperty("request", out _));
    }

    [Fact]
    public async Task Should_ReturnTheQbXmlBody_When_TheSharedKeyAsksForIt()
    {
        // T-910: the shared Api:ApiKey stands in as an all-scopes client, so it does hold qb:debug. A client key
        // without that scope is refused the body instead - see ClientScopeApiTests.
        var (status, body) = await PostAsync(Request(Row()), "?includeQbXml=true");

        Assert.Equal(HttpStatusCode.OK, status);
        var qbXml = body.GetProperty("qbXml");
        Assert.Contains("CheckAddRq", qbXml.GetProperty("request").GetString());
        Assert.False(qbXml.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task Should_Answer401_When_NoApiKeyIsSent()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/quickbooks/transactions/validate", Request(Row()), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_factory.Gateway.Requests);
    }

    [Fact]
    public async Task Should_HoldTheRowButKeepTheBatch_When_OneAmountIsOverTheLimit()
    {
        var (status, body) = await PostAsync(Request(Row(), Row(externalId: "row-2", amount: 200000m, memo: "BIG ONE")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(1, body.GetProperty("counts").GetProperty("wouldPost").GetInt32());
        Assert.Contains("amount-over-limit", Row(body, "row-2").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Should_ReadTheLimitsFromConfiguration_When_TheyAreSet()
    {
        using var host = _factory.WithSetting("QuickBooks:MaxLineAmount", "100.00");
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var response = await client.PostAsJsonAsync(
            "/api/v1/quickbooks/transactions/validate", Request(Row()), ApiFactory.Json);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("amount-over-limit", Row(body, "row-1").GetProperty("reason").GetString());
    }
}
