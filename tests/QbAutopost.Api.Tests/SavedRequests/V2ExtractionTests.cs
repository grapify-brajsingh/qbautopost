using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Api.Extraction;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1212b (dashboard-api-m12 6): <c>POST /api/v2/statement-extractions</c> end to end, through the real model client
/// and <see cref="FakeOpenRouterHandler"/>. One test per §6.1 row and per §6.9 problem.
/// </summary>
public sealed class V2ExtractionTests : IDisposable
{
    private readonly List<ExtractionTestHost> _hosts = [];

    public void Dispose() => _hosts.ForEach(h => h.Dispose());

    private ExtractionTestHost Host(IDictionary<string, string?>? settings = null)
    {
        var host = new ExtractionTestHost(settings);
        _hosts.Add(host);
        return host;
    }

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Response)> UploadAsync(
        ExtractionTestHost host, byte[] bytes, string role = DashboardRoles.Editor)
    {
        using var client = host.User(role);
        var (response, body) = await host.PostAsync(client, ExtractionTestHost.Upload(bytes));
        return (response.StatusCode, body, response);
    }

    private static void AssertProblem(JsonElement body, string type) =>
        Assert.Equal(type, body.GetProperty("type").GetString());

    // ---- the 200 answer (6.4, 6.8) -----------------------------------------------------------------------------------

    [Fact]
    public async Task Should_Return200WithTidiedPayload_When_StatementRead()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        var pdf = ExtractionTestHost.ChasePdf();

        var (status, body, _) = await UploadAsync(host, pdf);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            new[] { "extractionId", "file", "model", "promptVersion", "extractedUtc", "expiresUtc", "isBankStatement", "payload", "possibleDuplicates" },
            body.EnumerateObject().Select(p => p.Name));
        var id = Guid.Parse(body.GetProperty("extractionId").GetString()!);
        var file = body.GetProperty("file");
        Assert.Equal("chase-aug-2026.pdf", file.GetProperty("name").GetString());
        Assert.Equal(pdf.Length, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(), file.GetProperty("sha256").GetString());
        Assert.True(file.GetProperty("pageCount").GetInt32() >= 1);
        Assert.Empty(file.GetProperty("unreadPages").EnumerateArray());
        Assert.Equal("~google/gemini-pro-latest", body.GetProperty("model").GetString());
        Assert.Equal("2026-10-28.api-1", body.GetProperty("promptVersion").GetString());
        var extracted = body.GetProperty("extractedUtc").GetDateTime();
        Assert.Equal(extracted.AddHours(24), body.GetProperty("expiresUtc").GetDateTime());
        Assert.True(body.GetProperty("isBankStatement").GetBoolean());
        Assert.Empty(body.GetProperty("possibleDuplicates").EnumerateArray());

        var expected = JsonNode.Parse("""
            {
              "reference": "Statement 2026-08",
              "controlTotal": null,
              "transactions": [
                { "externalId": "row-1", "kind": "Deposit", "date": "2026-08-03", "account": "Chase Checking - 4521", "lineAccount": "Merchant Card Deposits", "memo": "CARD SETTLEMENT 0803", "payee": "Bankcard Merchant Services", "amount": 2450.00 },
                { "externalId": "row-2", "kind": "Check", "date": "2026-08-05", "account": "Chase Checking - 4521", "lineAccount": "Supplies & Materials", "memo": "POS PURCHASE RESTAURANT DEPOT", "last4": "7788", "payee": "Restaurant Depot", "amount": 318.40 },
                { "externalId": "row-3", "kind": "Check", "date": "2026-08-12", "account": "Chase Checking - 4521", "lineAccount": "Rent Expense", "memo": "CHECK 1043", "payee": "Harbor Properties", "checkNo": "1043", "amount": 1200.00 },
                { "externalId": "row-4", "kind": "Check", "date": "2026-08-31", "account": "Chase Checking - 4521", "lineAccount": "Bank Service Charges", "memo": "MONTHLY SERVICE FEE", "payee": "Bank Fees", "amount": 15.00 }
              ]
            }
            """);
        var payload = JsonNode.Parse(body.GetProperty("payload").GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, payload), payload!.ToJsonString());
        Assert.Equal("318.40", body.GetProperty("payload").GetProperty("transactions")[1].GetProperty("amount").GetRawText());

        var stored = Assert.Single(host.Store.Extractions);
        Assert.Equal(id, stored.Id);
        Assert.Null(stored.SavedRequestId);
        Assert.True(JsonNode.DeepEquals(expected, stored.Payload));
    }

    [Fact]
    public async Task Should_TidyWithoutDefaults_When_ReplyHasBlanksAndBadAmounts()
    {
        var host = Host();
        host.Handler.ReplyFixture("messy-rows.reply.json");

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, status);
        var expected = JsonNode.Parse("""
            {
              "reference": null,
              "controlTotal": null,
              "transactions": [
                { "externalId": "row-1", "kind": "Deposit", "date": "2026-08-03", "account": "Sample Bank - 1111", "payee": "Counter Deposit", "amount": 1240.10 },
                { "externalId": "row-2", "kind": "Check", "date": "08/05/2026", "amount": 60.00 },
                { "externalId": "row-3", "kind": "Check", "memo": "Amount printed as text" },
                { "externalId": "row-4", "kind": "Check", "lineAccount": "Supplies", "last4": "4521", "amount": 5 },
                { "externalId": "row-5", "kind": "Deposit", "amount": 20.5 }
              ]
            }
            """);
        var payload = JsonNode.Parse(body.GetProperty("payload").GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, payload), payload!.ToJsonString());
    }

    [Fact]
    public async Task Should_SendPromptSchemaModelAndText_When_Calling()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        var prompt = ExtractionPrompt.Load();

        var (status, _, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, status);
        var sent = Assert.Single(host.Handler.Requests);
        Assert.Equal("https://hermes.invalid:8642/v1/chat/completions", sent.Uri!.ToString());
        Assert.Equal("Bearer " + ExtractionTestHost.Key, sent.Authorization);
        var json = sent.Json;
        Assert.Equal("~google/gemini-pro-latest", json["model"]!.GetValue<string>());
        Assert.Equal(0m, json["temperature"]!.GetValue<decimal>());
        var messages = json["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal(prompt.SystemPrompt, messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
        var text = messages[1]!["content"]!.GetValue<string>();
        Assert.Contains("TRANSACTION DETAIL", text, StringComparison.Ordinal);
        Assert.Contains("Palm Court Rentals", text, StringComparison.Ordinal);
        var format = json["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("bank_statement", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(prompt.Schema, format["json_schema"]!["schema"]));
    }

    [Fact]
    public async Task Should_UseExtractionModel_When_ConfiguredApartFromJobModel()
    {
        var host = Host(With(("Extraction:Model", "acme/reader-1"), ("OpenRouter:Model", "acme/jobs-1")));
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("acme/reader-1", body.GetProperty("model").GetString());
        Assert.Equal("acme/reader-1", Assert.Single(host.Handler.Requests).Json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Should_Return422NotABankStatement_When_ModelSaysFalse()
    {
        var host = Host();
        host.Handler.ReplyFixture("not-a-statement.reply.json");

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        AssertProblem(body, "not-a-bank-statement");
        Assert.Equal("This PDF doesn't look like a bank or card statement.", body.GetProperty("detail").GetString());
        Assert.Empty(host.Store.Extractions);
    }

    [Fact]
    public async Task Should_Return200EmptyRows_When_StatementHasNoRows()
    {
        var host = Host();
        host.Handler.ReplyFixture("no-rows.reply.json");

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body.GetProperty("payload").GetProperty("transactions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("payload").GetProperty("controlTotal").ValueKind);
        Assert.Single(host.Store.Extractions);
    }

    // ---- the 6.1 table, in its order ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_Return503_When_ExtractionDisabled()
    {
        var host = Host(With(("Extraction:Enabled", "false")));

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertProblem(body, "extraction-unavailable");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return503_When_OpenRouterDisabled()
    {
        var host = Host(With(("OpenRouter:Enabled", "false")));

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertProblem(body, "extraction-unavailable");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return503_When_NoKey()
    {
        var host = Host(With(("OpenRouter:ApiKey", "")));

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertProblem(body, "extraction-unavailable");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return400_When_NoFilePart()
    {
        var host = Host();
        using var client = host.User(DashboardRoles.Editor);

        var (response, body) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf(), part: "other"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertProblem(body, "invalid-request");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return400_When_BodyIsNotMultipart()
    {
        var host = Host();
        using var client = host.User(DashboardRoles.Editor);

        var (response, body) = await host.PostAsync(client, SavedRequestTestHost.Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertProblem(body, "invalid-request");
    }

    [Fact]
    public async Task Should_Return413_When_FileOverMaxBytes()
    {
        var host = Host(With(("Extraction:MaxBytes", "1000")));

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        AssertProblem(body, "file-too-large");
        Assert.Contains("1000 bytes", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return415_When_FileDoesNotStartWithPdfPrefix()
    {
        var host = Host();

        var (status, body, _) = await UploadAsync(host, Encoding.ASCII.GetBytes("Date,Amount\n2026-09-01,10.00\n"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        AssertProblem(body, "not-a-pdf");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return415_When_PdfCannotBeOpened()
    {
        var host = Host();

        var (status, body, _) = await UploadAsync(host, Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not really a pdf\n"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        AssertProblem(body, "not-a-pdf");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return422_When_PdfProtected()
    {
        var host = Host();

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.EncryptedPdf());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        AssertProblem(body, "pdf-protected");
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return413_When_MorePagesThanMaxPages()
    {
        var host = Host(With(("Extraction:MaxPages", "1")));

        var (status, body, _) = await UploadAsync(
            host, ExtractionTestHost.Pdf(ExtractionTestHost.TextPage(), ExtractionTestHost.TextPage()));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        AssertProblem(body, "file-too-large");
        Assert.Contains("1 pages", body.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return422NoText_When_EveryPageShort()
    {
        var host = Host();

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.Pdf(["Page 1"], ["Page 2"]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        AssertProblem(body, "no-text");
        Assert.Equal("This PDF has no readable text. It may be a scan.", body.GetProperty("detail").GetString());
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return429_When_ExtractionInFlight()
    {
        var host = Host();
        var release = new TaskCompletionSource();
        host.Handler.ReplyAfter(release.Task, File.ReadAllText(Fixtures.PathOf("extractions", "chase-checking-4521.reply.json")));
        var token = host.TokenFor(DashboardRoles.Editor);
        using var first = host.ClientWithToken(token);
        using var second = host.ClientWithToken(token);
        var arrived = host.Handler.Arrived.Task;

        var running = host.PostAsync(first, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));
        await arrived.WaitAsync(TimeSpan.FromSeconds(10));
        var (refused, body) = await host.PostAsync(second, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));
        release.SetResult();
        var (done, _) = await running;

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        AssertProblem(body, "too-many-extractions");
        Assert.Equal("30", refused.Headers.RetryAfter?.ToString());
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return429WithRetryAfter_When_HourlyLimitReached()
    {
        var host = Host(With(("Extraction:MaxPerUserPerHour", "1")));
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        var token = host.TokenFor(DashboardRoles.Editor);
        using var client = host.ClientWithToken(token);

        var (ok, _) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));
        var (refused, body) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        AssertProblem(body, "too-many-extractions");
        var seconds = int.Parse(refused.Headers.RetryAfter!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 3500, 3600);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_Return429BeforeReadingThePdf_When_HourlyLimitReached()
    {
        var host = Host(With(("Extraction:MaxPerUserPerHour", "1")));
        var token = host.TokenFor(DashboardRoles.Editor);
        using var client = host.ClientWithToken(token);

        var (first, _) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.Pdf(["Page 1"])));
        var (refused, body) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.Pdf(["Page 1"])));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        AssertProblem(body, "too-many-extractions");
    }

    [Fact]
    public async Task Should_CountUsersApart_When_AnotherUserHitHourlyLimit()
    {
        var host = Host(With(("Extraction:MaxPerUserPerHour", "1")));
        host.Handler.ReplyFixture("chase-checking-4521.reply.json").ReplyFixture("chase-checking-4521.reply.json");

        var (first, _, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());
        var (second, _, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, second);
    }

    // ---- the model's failures (6.2, 6.9) -----------------------------------------------------------------------------

    [Fact]
    public async Task Should_Return502_When_ReplyInvalidTwice()
    {
        var host = Host();
        host.Handler.ReplyFixture("invalid.reply.json").ReplyContent("not json at all");

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.BadGateway, status);
        AssertProblem(body, "extraction-failed");
        Assert.Equal(2, host.Handler.Requests.Count);
        Assert.Contains("Your previous reply was rejected", host.Handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Empty(host.Store.Extractions);
    }

    [Fact]
    public async Task Should_Return200_When_ReplyValidOnRetry()
    {
        var host = Host();
        host.Handler.ReplyFixture("invalid.reply.json").ReplyFixture("chase-checking-4521.reply.json");

        var (status, _, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Should_Return502_When_OpenRouterErrors()
    {
        var host = Host();
        host.Handler.ReplyStatus(HttpStatusCode.InternalServerError).ReplyFixture("chase-checking-4521.reply.json");

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.BadGateway, status);
        AssertProblem(body, "extraction-failed");
        Assert.Single(host.Handler.Requests);
        Assert.Empty(host.Store.Extractions);
    }

    [Fact]
    public async Task Should_Return504_When_NoAnswerInTimeout()
    {
        var host = Host(With(("Extraction:TimeoutSeconds", "1")));
        host.Handler.ReplyNever();

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        AssertProblem(body, "extraction-timeout");
        Assert.True(host.Handler.SawCancellation);
        Assert.Empty(host.Store.Extractions);
    }

    [Fact]
    public async Task Should_CancelModelCall_When_ClientDisconnects()
    {
        var host = Host();
        host.Handler.ReplyNever();
        using var client = host.User(DashboardRoles.Editor);
        using var cts = new CancellationTokenSource();
        var arrived = host.Handler.Arrived.Task;

        var call = client.PostAsync(ExtractionTestHost.Route, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()), cts.Token);
        await arrived.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!host.Handler.SawCancellation && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(host.Handler.SawCancellation);
        Assert.Empty(host.Store.Extractions);
    }

    // ---- who may call ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_Return403_When_Viewer()
    {
        var host = Host();

        var (status, _, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf(), DashboardRoles.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task Should_AllowAdmin_When_SignedIn()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");

        var (status, _, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf(), DashboardRoles.Admin);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Should_Return403_When_ApiKeyClient()
    {
        var host = Host();
        using var n8n = host.ApiKeyClient(host.Inner.N8nKey);
        using var legacy = host.ApiKeyClient(ApiFactory.ApiKey);

        var (fromN8n, _) = await host.PostAsync(n8n, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));
        var (fromLegacy, _) = await host.PostAsync(legacy, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.Forbidden, fromN8n.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fromLegacy.StatusCode);
        Assert.Empty(host.Handler.Requests);
    }

    // ---- records and duplicates (6.6, 6.7, 7.3) ----------------------------------------------------------------------

    [Fact]
    public async Task Should_ListSameFile_When_SavedFileUploadedAgain()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json").ReplyFixture("chase-checking-4521.reply.json");
        var token = host.TokenFor(DashboardRoles.Editor);
        var userId = host.Factory.Users.IdOf(token);
        using var client = host.ClientWithToken(token);
        var pdf = ExtractionTestHost.ChasePdf();

        var (first, firstBody) = await host.PostAsync(client, ExtractionTestHost.Upload(pdf));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var extraction = (await host.Store.GetExtractionAsync(
            Guid.Parse(firstBody.GetProperty("extractionId").GetString()!), default))!;
        // Ruling R2: saved through the store; the v2 save route arrives in T-1213.
        var savedId = await SaveUploadAsync(host.Store, extraction, userId);

        var (second, body) = await host.PostAsync(client, ExtractionTestHost.Upload(pdf));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var hit = Assert.Single(body.GetProperty("possibleDuplicates").EnumerateArray());
        Assert.Equal(savedId, hit.GetProperty("id").GetInt64());
        Assert.Equal($"SR-{savedId:000000}", hit.GetProperty("publicRef").GetString());
        Assert.Equal("Statement 2026-08", hit.GetProperty("reference").GetString());
        Assert.Equal("pending", hit.GetProperty("status").GetString());
        Assert.Equal("upload", hit.GetProperty("source").GetString());
        Assert.Contains("same-file", hit.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.String, hit.GetProperty("receivedUtc").ValueKind);
    }

    [Fact]
    public async Task Should_DeleteExpiredUnsaved_When_NewExtractionCreated()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        var old = DateTime.UtcNow.AddDays(-2);
        var expired = new StatementExtraction(
            Guid.NewGuid(), "someone", "old.pdf", 10, new string('a', 64), 1, "m", "v", new JsonObject { ["transactions"] = new JsonArray() },
            old, old.AddHours(24), null);
        await host.Store.CreateExtractionAsync(expired, default);

        var (status, body, _) = await UploadAsync(host, ExtractionTestHost.ChasePdf());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(await host.Store.GetExtractionAsync(expired.Id, default));
        Assert.Equal(body.GetProperty("extractionId").GetString(), Assert.Single(host.Store.Extractions).Id.ToString());
    }

    [Fact]
    public async Task Should_StoreSanitisedName_When_FileNameIsUnsafe()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        using var client = host.User(DashboardRoles.Editor);

        var (response, body) = await host.PostAsync(
            client, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf(), fileName: "..\\..\\secret\\aug.pdf"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("aug.pdf", body.GetProperty("file").GetProperty("name").GetString());
        Assert.Equal("aug.pdf", Assert.Single(host.Store.Extractions).FileName);
    }

    // ---- nothing kept, nothing leaked (anti-goals 4 and 5) ----------------------------------------------------------

    [Fact]
    public async Task Should_LeaveNoTraceOfPdfText_When_Extracted()
    {
        const string Marker = "ZZMARKER-7731";
        var host = Host();
        host.Handler.ReplyFixture("marker.reply.json");
        var started = DateTime.UtcNow.AddSeconds(-1);
        var pdf = ExtractionTestHost.Pdf(ExtractionTestHost.TextPage("memo " + Marker));

        var (status, body, _) = await UploadAsync(host, pdf);

        Assert.Equal(HttpStatusCode.OK, status);
        // It did reach the model, so its absence below means something.
        Assert.Contains(Marker, Assert.Single(host.Handler.Requests).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, body.GetRawText(), StringComparison.Ordinal);
        foreach (var record in host.Store.Extractions)
        {
            Assert.DoesNotContain(Marker, JsonSerializer.Serialize(record), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Marker, host.LogText, StringComparison.Ordinal);
        foreach (var path in FilesWrittenSince(started, host.Factory.Dir.Path))
        {
            Assert.DoesNotContain(Marker, ReadText(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Should_NeverExposeKey_When_AnyOutcome()
    {
        var host = Host(With(("Extraction:TimeoutSeconds", "1")));
        host.Handler
            .ReplyFixture("chase-checking-4521.reply.json")
            .ReplyStatus(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""")
            .ReplyFixture("invalid.reply.json").ReplyFixture("invalid.reply.json")
            .ReplyFixture("not-a-statement.reply.json")
            .ReplyNever();
        var answers = new StringBuilder();

        for (var i = 0; i < 5; i++)
        {
            var (_, body, response) = await UploadAsync(host, ExtractionTestHost.ChasePdf());
            answers.Append(body.GetRawText()).Append(response.Headers).Append(response.Content.Headers);
        }

        Assert.All(host.Handler.Requests, r => Assert.Equal("Bearer " + ExtractionTestHost.Key, r.Authorization));
        Assert.DoesNotContain("MARKERKEY", answers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MARKERKEY", host.LogText, StringComparison.Ordinal);
        foreach (var record in host.Store.Extractions)
        {
            Assert.DoesNotContain("MARKERKEY", JsonSerializer.Serialize(record), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Should_LogOneLine_When_Extracted()
    {
        var host = Host();
        host.Handler.ReplyFixture("chase-checking-4521.reply.json");
        var token = host.TokenFor(DashboardRoles.Editor);
        using var client = host.ClientWithToken(token);

        var (response, _) = await host.PostAsync(client, ExtractionTestHost.Upload(ExtractionTestHost.ChasePdf()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(host.Logs.Events, e =>
            e.Properties.TryGetValue("SourceContext", out var c) && c.ToString().Contains(nameof(StatementExtractionService), StringComparison.Ordinal));
        Assert.Equal($"\"{host.Factory.Users.IdOf(token)}\"", line.Properties["UserId"].ToString());
        Assert.Equal("4", line.Properties["RowCount"].ToString());
        Assert.Equal("\"ok\"", line.Properties["Outcome"].ToString());
        Assert.Equal("\"2026-10-28.api-1\"", line.Properties["PromptVersion"].ToString());
        Assert.Equal("\"~google/gemini-pro-latest\"", line.Properties["Model"].ToString());
        Assert.True(line.Properties.ContainsKey("SizeBytes") && line.Properties.ContainsKey("PageCount")
            && line.Properties.ContainsKey("ElapsedMs"));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static async Task<long> SaveUploadAsync(FakeSavedRequestStore store, StatementExtraction extraction, string userId)
    {
        var now = DateTime.UtcNow;
        var payload = JsonSerializer.SerializeToElement(extraction.Payload);
        var request = new NewSavedRequest(
            userId, userId, payload.GetRawText(), payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, now, 1, null, [], [], null),
            new DuplicateMatch(null, []), now);
        var result = await store.SaveUploadAsync(
            new NewUploadSave(request, extraction.Id, userId, payload, null, null, $"Read from {extraction.FileName}", "Reviewed before saving"),
            default);
        Assert.Equal(UploadSaveKind.Created, result.Kind);
        return result.Record!.Id;
    }

    /// <summary>The host's own folder (data, logs, output) and every file directly in the temp folder, written since <paramref name="since"/>.</summary>
    private static IEnumerable<string> FilesWrittenSince(DateTime since, string hostDir)
    {
        var files = Directory.EnumerateFiles(hostDir, "*", SearchOption.AllDirectories).ToList();
        foreach (var root in new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                files.AddRange(Directory.EnumerateFiles(root).Where(f => SafeWriteTime(f) >= since));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static string ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 64 * 1024 * 1024)
            {
                return "";
            }

            using var reader = new StreamReader(stream, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
