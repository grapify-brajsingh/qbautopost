using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QbAutopost.Api.Database;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Data;

namespace QbAutopost.Api.Tests.Database;

/// <summary>Saved-requests spec 5.5, 10.6, 10.7 through the real host, with the gate forced to a state.</summary>
public sealed class DatabaseHealthTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Should_KeepValidateAndPostWorking_When_GateUnreachable()
    {
        _factory.Gate.Set(DatabaseState.Unreachable);
        using var client = _factory.CreateAuthorizedClient();
        var body = new
        {
            reference = "zemen-2019-12",
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
                    memo = "HOME DEPOT #6412 PURCHASE",
                    last4 = "4521",
                },
            },
        };

        using var response = await client.PostAsJsonAsync("/api/v1/quickbooks/transactions/validate-and-post", body, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Should_ReportDatabaseInReadiness_When_Unreachable()
    {
        _factory.Gate.Set(DatabaseState.Unreachable);
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/ready");

        // Readiness is per feature (spec 10.7, Q-83): the database is reported, the service stays ready.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checks = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks").EnumerateArray().ToList();
        var database = Assert.Single(checks, c => c.GetProperty("name").GetString() == "database");
        Assert.False(database.GetProperty("ok").GetBoolean());
        Assert.Equal("unreachable", database.GetProperty("reason").GetString());
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "rules" && c.GetProperty("ok").GetBoolean());
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "worker");
    }

    [Fact]
    public async Task Should_ReportSchemaOutOfDateInReadiness_When_SchemaIsBehind()
    {
        _factory.Gate.Set(DatabaseState.SchemaOutOfDate);
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        var checks = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks").EnumerateArray();
        var database = Assert.Single(checks, c => c.GetProperty("name").GetString() == "database");
        Assert.Equal("schema-out-of-date", database.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Should_Answer200_When_DatabaseIsCurrent()
    {
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/health/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("schemaCurrent").GetBoolean());
    }

    [Fact]
    public async Task Should_Answer503WithReason_When_DatabaseIsDown()
    {
        _factory.Schema = new SchemaStatus(false, false, [], SchemaState.Unreachable);
        _factory.Gate.Set(DatabaseState.Unreachable);
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/health/database");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("unreachable", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Should_NoticeTheDatabaseIsBack_When_HealthDatabaseIsAsked()
    {
        _factory.Gate.Set(DatabaseState.Unreachable);
        using var client = _factory.CreateAuthorizedClient();

        using var response = await client.GetAsync("/api/v1/health/database");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DatabaseState.Ready, _factory.Gate.State);
    }

    [Fact]
    public async Task Should_NeverIncludeConnectionString_When_HealthDatabaseAnswers()
    {
        const string secret = "Host=db.internal;Database=qb;Username=app;Password=hunter2";
        using var host = _factory.WithSetting("Database:ConnectionString", secret);
        _factory.Schema = new SchemaStatus(false, false, [], SchemaState.Unreachable);
        _factory.Gate.Set(DatabaseState.Unreachable);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);

        using var down = await client.GetAsync("/api/v1/health/database");
        var downText = await down.Content.ReadAsStringAsync();
        using var ready = await client.GetAsync("/api/v1/health/ready");
        var readyText = await ready.Content.ReadAsStringAsync();

        foreach (var text in new[] { downText, readyText })
        {
            Assert.DoesNotContain("Password=", text);
            Assert.DoesNotContain("Host=", text);
            Assert.DoesNotContain("hunter2", text);
        }
    }

    [Fact]
    public async Task Should_RequireAKey_When_HealthDatabaseIsAskedWithout()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/database");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Should_MarkPostingRequestsUnknown_When_HostStarts()
    {
        var seeded = await InFlightRecoveryTests.SeedPostingAsync(_factory.Store);

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await _factory.Store.GetAsync(seeded.Id, default);
        Assert.Equal(SavedRequestStatus.Unknown, after!.Status);
    }
}
