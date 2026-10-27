using Microsoft.Extensions.DependencyInjection;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Data.Tests;

/// <summary>
/// T-1016 (spec 5.4): the whole store contract again, connected as <c>qbautopost_app</c> with only the grants of
/// <c>deploy/grants.sql</c>. If the store needs a statement the grants refuse, a save, claim or complete fails here
/// rather than on the server. The schema is migrated and granted as the superuser, as deploy/apply-migrations.ps1 does.
/// </summary>
[RequiresPostgres]
public sealed class AppRoleSavedRequestStoreTests(PostgresFixture postgres)
    : SavedRequestStoreContract, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ServiceProvider? _services;
    private string? _appConnection;

    public async Task InitializeAsync()
    {
        if (!postgres.Available)
        {
            return;
        }

        var (_, app) = await postgres.CreateAppRoleDatabaseAsync();
        _appConnection = app;
        _services = new ServiceCollection().AddQbAutopostData(app, commandTimeoutSeconds: 30).BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_appConnection is not null)
        {
            Api.TestDatabase.Release(_appConnection);
        }
    }

    protected override ISavedRequestStore CreateStore() =>
        _services?.GetRequiredService<ISavedRequestStore>() ?? throw new InvalidOperationException(postgres.SkipReason);
}
