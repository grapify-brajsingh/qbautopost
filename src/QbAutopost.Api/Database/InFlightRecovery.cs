namespace QbAutopost.Api.Database;

/// <summary>
/// Spec 10.6: the first database check runs before the host serves anything. Recovery of requests left
/// <c>posting</c> is a step of that check inside <see cref="DatabaseGate"/> (the gate is not Ready until it has
/// succeeded), so there is nothing to race: hosted services finish starting before Kestrel binds, and a database that
/// comes up later becomes Ready only through a refresh that recovers first.
/// </summary>
public sealed class InFlightRecovery(DatabaseGate gate) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!gate.Checked)
        {
            await gate.RefreshAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
