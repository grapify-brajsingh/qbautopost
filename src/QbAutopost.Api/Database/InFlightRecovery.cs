using QbAutopost.Core.Abstractions;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Database;

/// <summary>
/// Spec 10.6: before the API serves the saved-request routes, every request still <c>posting</c> becomes
/// <c>unknown</c>; nothing is retried, because the post may have landed. Hosted services finish starting before
/// Kestrel binds, so <see cref="StartAsync"/> completing is what "before the routes" means. It runs once: when the
/// database is not ready then, it runs the first time the gate becomes ready. There is no timer.
/// </summary>
public sealed class InFlightRecovery(
    DatabaseGate gate, ISavedRequestStore store, IClock clock, ILogger<InFlightRecovery> log) : IHostedService
{
    private int _done;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (gate.State == DatabaseState.NotConfigured)
        {
            return;
        }

        if (!gate.Checked)
        {
            await gate.RefreshAsync(cancellationToken);
        }

        if (gate.IsReady)
        {
            await RunOnceAsync(cancellationToken);
            return;
        }

        log.LogWarning(
            "Database is {Reason}; interrupted saved-request posts are closed when it becomes ready", gate.Reason);
        gate.BecameReady += OnBecameReady;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        gate.BecameReady -= OnBecameReady;
        return Task.CompletedTask;
    }

    private void OnBecameReady() => _ = RunOnceAsync(CancellationToken.None);

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _done, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var moved = await store.RecoverInFlightAsync(clock.UtcNow, ct);
            log.LogInformation("Saved-request recovery: {Count} interrupted post(s) marked unknown", moved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Allow the next ready transition to try again rather than leaving requests stuck in posting.
            Interlocked.Exchange(ref _done, 0);
            log.LogError(ex, "Saved-request recovery failed");
        }
    }
}
