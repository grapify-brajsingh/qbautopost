using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using QbAutopost.Core.Companies;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>T-1703: the company store contract on a real PostgreSQL 16, through the migrations.</summary>
[RequiresPostgres]
public sealed class EfCompanyStoreTests(PostgresFixture postgres)
    : CompanyStoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider? _services;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        var connectionString = _connectionString = await postgres.CreateDatabaseAsync();
        _services = new ServiceCollection().AddQbAutopostData(connectionString, commandTimeoutSeconds: 30).BuildServiceProvider();
        await PostgresFixture.MigrateAsync(connectionString);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_connectionString is not null)
        {
            Api.TestDatabase.Release(_connectionString);
        }
    }

    protected override ICompanyStore CreateStore() =>
        _services?.GetRequiredService<ICompanyStore>() ?? throw new InvalidOperationException(postgres.SkipReason);

    protected override async Task<long> SeedUnassignedRequestAsync()
    {
        var store = _services?.GetRequiredService<ISavedRequestStore>() ?? throw new InvalidOperationException(postgres.SkipReason);
        var body = """{"reference":"R1","controlTotal":10.00,"transactions":[{"externalId":"e1","kind":"check","date":"2026-10-01","amount":10.00,"account":"Checking 1234","payee":"Acme","memo":"rent"}]}""";
        var payload = JsonDocument.Parse(body).RootElement.Clone();
        var request = new NewSavedRequest(
            "api-client:n8n", "api-client:n8n", body, payload, SavedPayloadReader.Read(payload), PayloadCanonicalizer.Sha256(payload),
            new HashSet<string>(), null, new ValidationSnapshot(ValidationState.Ok, Now, 1, null, [], [], null),
            new DuplicateMatch(null, []), Now);
        return (await store.CreateAsync(request, default)).Record.Id;
    }

    protected override async Task<(int? CompanyId, CompanySource? Source)> ReadRequestCompanyAsync(long requestId)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT company_id, company_source FROM saved_requests WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", requestId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var source = reader.IsDBNull(1) ? (CompanySource?)null : KebabCaseEnumConverter<CompanySource>.FromText(reader.GetString(1));
        return (reader.IsDBNull(0) ? null : reader.GetInt32(0), source);
    }
}
