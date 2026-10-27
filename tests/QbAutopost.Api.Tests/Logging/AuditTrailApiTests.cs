using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using QbAutopost.Api.Logging;
using QbAutopost.Api.Security;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Tests.Logging;

/// <summary>
/// T-912 / FR-A-16 and api-v1 §2.6 through the host: the correlation id a caller can quote back, and one audit line
/// per authenticated mutating request.
/// <para>
/// The two properties that matter: <b>every posting leaves a line naming who asked for it</b>, and <b>that line never
/// carries the key, the body or any statement text</b> (CLAUDE.md rule 6).
/// </para>
/// </summary>
public sealed class AuditTrailApiTests : IDisposable
{
    private const string PostRoute = "/api/v1/quickbooks/transactions";

    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string LogDir => _factory.Dir.Combine("data", "logs");

    private string ClientsFile => _factory.Dir.Combine("data", "clients.json");

    private static object Request(bool? dryRun = false, string memo = "HOME DEPOT #6412 PURCHASE") => new
    {
        reference = "payroll-2026-09",
        dryRun,
        controlTotal = 184.32m,
        transactions = new[]
        {
            new
            {
                externalId = "row-1",
                kind = "Check",
                date = "2026-08-05",
                amount = 184.32m,
                account = "Chase Checking 4521",
                payee = "HOME DEPOT #6412",
                memo,
                last4 = "4521",
            },
        },
    };

    private string AuditText()
    {
        var files = Directory.Exists(LogDir)
            ? Directory.GetFiles(LogDir, AuditLog.FilePrefix + "*" + AuditLog.FileExtension)
            : [];
        return string.Join('\n', files.SelectMany(File.ReadAllLines));
    }

    private JsonElement[] AuditLines() =>
        AuditText().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToArray();

    private string[] LogLines()
    {
        var file = Assert.Single(Directory.GetFiles(LogDir, LoggingSetup.FilePrefix + "*.log"));
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Writes <c>clients.json</c> with one caller and returns the key they were issued.</summary>
    private string GiveClientAKey(params string[] scopes)
    {
        var key = ApiClientStore.NewKey();
        var secret = ApiClientStore.NewSecret(key);
        Directory.CreateDirectory(Path.GetDirectoryName(ClientsFile)!);
        new ApiClientStore(ClientsFile).Save(new ApiClientList
        {
            Clients =
            [
                new ApiClient
                {
                    Id = "acme-erp",
                    Name = "Acme ERP",
                    KeyHash = secret.KeyHash,
                    KeySalt = secret.KeySalt,
                    Scopes = scopes,
                    CreatedUtc = DateTime.UtcNow.AddDays(-1),
                },
            ],
        });

        return key;
    }

    private static HttpClient WithKey(WebApplicationFactory<Program> host, string key)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task Should_EchoAGeneratedRequestId_When_NoneIsSent()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health");

        var id = Assert.Single(response.Headers.GetValues(RequestId.Header));
        Assert.Equal(26, id.Length);
    }

    [Fact]
    public async Task Should_EchoTheInboundRequestId_When_ItIsWellFormed()
    {
        using var client = _factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Add(RequestId.Header, "caller-request-0042");

        using var response = await client.GetAsync("/api/v1/jobs");

        Assert.Equal("caller-request-0042", Assert.Single(response.Headers.GetValues(RequestId.Header)));
    }

    [Fact]
    public async Task Should_GenerateAFreshRequestId_When_TheInboundOneIsMalformed()
    {
        using var client = _factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(RequestId.Header, "no good!");

        using var response = await client.GetAsync("/api/v1/jobs");

        var id = Assert.Single(response.Headers.GetValues(RequestId.Header));
        Assert.Equal(26, id.Length);
    }

    [Fact]
    public async Task Should_CarryTheRequestIdIntoTheProblem_When_TheRequestIsRefused()
    {
        // api-v1 §2.6: a caller reporting a failure quotes the id from the body; it must be the one in the header.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/jobs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal(
            Assert.Single(response.Headers.GetValues(RequestId.Header)),
            problem.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Should_AppendOneAuditLine_When_TransactionsArePosted()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(AuditLines());
        Assert.Equal(Assert.Single(response.Headers.GetValues(RequestId.Header)), line.GetProperty("requestId").GetString());
        Assert.Equal(ApiKeyMiddleware.LegacyClientId, line.GetProperty("clientId").GetString());
        Assert.Equal("POST", line.GetProperty("method").GetString());
        Assert.Equal(PostRoute, line.GetProperty("path").GetString());
        Assert.Equal("ok", line.GetProperty("outcome").GetString());
        Assert.Equal(200, line.GetProperty("status").GetInt32());
        Assert.Equal("api-payroll-2026-09#1", line.GetProperty("batchId").GetString());
        Assert.Equal(1, line.GetProperty("counts").GetProperty("posted").GetInt32());
        Assert.Equal(184.32m, line.GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task Should_WriteNoAuditLine_When_TheRequestOnlyReads()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(AuditLines());
    }

    [Fact]
    public async Task Should_WriteNoAuditLine_When_TheCallerOnlyValidates()
    {
        // A validate changes nothing and reaches neither QuickBooks nor Hermes; auditing it would bury the postings.
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(PostRoute + "/validate", Request(dryRun: true), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(AuditLines());
    }

    [Fact]
    public async Task Should_KeepTheKeyAndTheBodyOutOfTheLine_When_APostIsAudited()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync(PostRoute, Request(memo: "ACH DEBIT FPL ELECTRIC UTILITY"), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = AuditText();
        Assert.DoesNotContain(ApiFactory.ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ACH DEBIT FPL ELECTRIC UTILITY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HOME DEPOT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Chase Checking", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_AuditTheRefusal_When_TheClientLacksTheScope()
    {
        // Trap 17: the key check returns early, so an audit sitting behind it would never see a 403 — and "who was
        // turned away from the posting route" is exactly what an auditor asks first.
        var key = GiveClientAKey(ApiScopes.QbRead);
        using var client = WithKey(_factory, key);

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var line = Assert.Single(AuditLines());
        Assert.Equal("acme-erp", line.GetProperty("clientId").GetString());
        Assert.Equal("refused", line.GetProperty("outcome").GetString());
        Assert.Equal(403, line.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Should_WriteNoAuditLine_When_TheKeyIsUnknown()
    {
        // FR-A-16 audits *authenticated* requests. A stranger must not be able to grow the money-evidence file;
        // the 401 is already in the operational log (Q-64).
        // A client must exist, or the host refuses to start with the shared key switched off (fail-closed, T-910).
        GiveClientAKey(ApiScopes.QbPost);
        using var host = _factory.WithSetting("Api:AllowLegacyKey", "false");
        using var client = WithKey(host, "not-a-real-key");

        using var response = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(AuditLines());
    }

    [Fact]
    public async Task Should_RecordTheIdempotencyKeyAndTheReplay_When_TheSameKeyIsSentTwice()
    {
        using var client = _factory.CreateAuthorizedClient();
        client.DefaultRequestHeaders.Add(IdempotencyMiddleware.KeyHeader, "acme-0042");

        using var first = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);
        using var second = await client.PostAsJsonAsync(PostRoute, Request(), ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var lines = AuditLines();
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal("acme-0042", l.GetProperty("idempotencyKey").GetString()));
        Assert.Equal("ok", lines[0].GetProperty("outcome").GetString());
        Assert.Equal("replayed", lines[1].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Should_AuditTheJob_When_AJobIsQueued()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.PostJobAsync(_factory.Dir.CopySampleJob());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var line = Assert.Single(AuditLines());
        Assert.Equal("2026-08-tropicana", line.GetProperty("jobId").GetString());
        Assert.Equal("accepted", line.GetProperty("outcome").GetString());
        Assert.Equal(202, line.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Should_TagEveryLogLineWithTheRequestIdAndClient_When_ARequestIsServed()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/jobs");

        var id = Assert.Single(response.Headers.GetValues(RequestId.Header));
        var requestLine = Assert.Single(LogLines(), l => l.Contains("HTTP GET /api/v1/jobs responded 200", StringComparison.Ordinal));
        Assert.Contains(id, requestLine, StringComparison.Ordinal);
        Assert.Contains(ApiKeyMiddleware.LegacyClientId, requestLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_TagLinesOutsideARequestWithDashes_When_TheHostStarts()
    {
        _ = _factory.Services;

        var startup = Assert.Single(LogLines(), l => l.Contains("QuickBooks gateway", StringComparison.Ordinal));
        Assert.Contains("[-] [-] [-]", startup, StringComparison.Ordinal);
    }
}
