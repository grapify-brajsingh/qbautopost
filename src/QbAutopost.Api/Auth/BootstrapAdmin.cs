using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.Database;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Security;
using QbAutopost.Data;
using QbAutopost.Data.Entities;

namespace QbAutopost.Api.Auth;

/// <summary>
/// Saved-requests spec 11.4: when the users table is empty at startup and <c>Auth:BootstrapAdmin:Email</c> and
/// <c>Password</c> are set, that admin is created with <c>mustChangePassword: true</c> and the creation is logged
/// (email only, never the password). When the table is not empty the settings are ignored and a warning asks that
/// they be removed.
/// <para>
/// It runs once, at start, after <see cref="InFlightRecovery"/> has given the database gate its first look. If the
/// gate is not Ready then (PostgreSQL still starting, schema not migrated) it logs a warning and does nothing: it never
/// throws, never retries on a timer and never holds the host up. Restart the service once the database is ready.
/// </para>
/// </summary>
public sealed class BootstrapAdmin(
    IServiceScopeFactory scopes,
    DatabaseGate gate,
    IOptions<AppSettings> settings,
    IOptions<IdentityOptions> identity,
    IClock clock,
    ILogger<BootstrapAdmin> log) : IHostedService
{
    public const string Actor = "bootstrap";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = settings.Value.Auth.BootstrapAdmin;
        var email = configured.Email.Trim();
        var password = configured.Password;
        if (email.Length == 0 && password.Length == 0)
        {
            return;
        }

        try
        {
            await RunAsync(email, password, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only: an Npgsql message can name the host, and nothing here may carry the password.
            log.LogError("BootstrapAdmin: the bootstrap admin was not created: {ExceptionType}", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(string email, string password, CancellationToken ct)
    {
        if (email.Length == 0 || password.Length == 0)
        {
            log.LogError("BootstrapAdmin: Auth:BootstrapAdmin needs both Email and Password; no admin was created");
            return;
        }

        if (!gate.Checked)
        {
            await gate.RefreshAsync(ct);
        }

        if (!gate.IsReady)
        {
            log.LogWarning(
                "BootstrapAdmin: not run, the database is not ready ({Reason}); restart the service once it is to create {Email}",
                gate.Reason, email);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QbAutopostDbContext>();
        if (await db.Users.AnyAsync(ct))
        {
            log.LogWarning(
                "BootstrapAdmin: Auth:BootstrapAdmin is set but users already exist, so it was ignored; remove Auth:BootstrapAdmin:Email and Password from the settings");
            return;
        }

        if (!UserProvisioning.IsEmail(email))
        {
            log.LogError("BootstrapAdmin: Auth:BootstrapAdmin:Email is not an email address; no admin was created");
            return;
        }

        if (password.Length < identity.Value.Password.RequiredLength)
        {
            log.LogError(
                "BootstrapAdmin: Auth:BootstrapAdmin:Password is shorter than {MinLength} characters; no admin was created",
                identity.Value.Password.RequiredLength);
            return;
        }

        var result = await UserProvisioning.CreateAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>(),
            db, email, email, DashboardRoles.Admin, password, Actor, clock.UtcNow, ct);
        if (result.Outcome != ProvisionOutcome.Created)
        {
            log.LogError("BootstrapAdmin: the bootstrap admin was not created: {Outcome}", result.Outcome);
            return;
        }

        await scope.ServiceProvider.GetRequiredService<UserAuditWriter>()
            .WriteAsync(result.User!.Id, Actor, UserAuditWriter.Created, new { role = DashboardRoles.Admin, source = "bootstrap" }, ct);
        log.LogInformation("BootstrapAdmin: created admin {Email}; they must change the password at first sign-in", email);
    }
}
