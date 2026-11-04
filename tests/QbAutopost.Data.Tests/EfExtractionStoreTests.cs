using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>T-1209: the extraction contract on a real PostgreSQL 16, through the migrations.</summary>
[RequiresPostgres]
public sealed class EfExtractionStoreTests(PostgresFixture postgres)
    : ExtractionStoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
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

    protected override (ISavedRequestStore Requests, IUploadStore Uploads) CreateStores()
    {
        var store = _services?.GetRequiredService<ISavedRequestStore>() ?? throw new InvalidOperationException(postgres.SkipReason);
        return (store, (IUploadStore)store);
    }

    protected override async Task<int> SeedCompanyAsync()
    {
        var companies = _services!.GetRequiredService<ICompanyStore>();
        var made = await companies.EnsureDefaultAsync(
            new NewDefaultCompany("Default company", @"C:\QB\Default.qbw", null, null), DateTime.UtcNow, default);
        return made.Company.Id;
    }

    protected override async Task<int?> StoredCompanyIdAsync(Guid extractionId)
    {
        var factory = _services!.GetRequiredService<IDbContextFactory<QbAutopostDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.StatementExtractions.Where(e => e.Id == extractionId).Select(e => e.CompanyId).SingleAsync();
    }
}
