using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Gateway;

/// <summary>FR-11 timing settings (<c>QuickBooks:BusyTimeoutSeconds</c>; the retry delay is 5 s by spec).</summary>
public sealed record QbGatewayPolicy
{
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Wraps the SDK gateway (FR-11): one call at a time for the whole process (the worker, undo, sync-lists and health
/// share it), and a call that runs longer than <see cref="QbGatewayPolicy.BusyTimeout"/> is abandoned with
/// <see cref="QuickBooksBusyException"/>. A COM call cannot be cancelled, so an abandoned call keeps the lock until it
/// really ends; a caller that cannot get the lock within the busy timeout gets <see cref="QuickBooksUnavailableException"/>
/// (nothing was sent).
/// </summary>
public sealed class ResilientQbGateway(IQbGateway inner, QbGatewayPolicy policy) : IQbGateway
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IQbGateway Inner => inner;

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
        RunExclusiveAsync(token => inner.ProcessAsync(qbxml, token), ct);

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        RunExclusiveAsync(inner.CurrentCompanyFileAsync, ct);

    private async Task<string> RunExclusiveAsync(Func<CancellationToken, Task<string>> call, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(policy.BusyTimeout, ct))
        {
            throw new QuickBooksUnavailableException("QuickBooks is still busy with an earlier call that did not finish");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<string>? task = null;
        try
        {
            task = call(cts.Token);
            return await task.WaitAsync(policy.BusyTimeout, ct);
        }
        catch (TimeoutException) when (task is { IsCompleted: false })
        {
            throw new QuickBooksBusyException(
                $"QuickBooks did not answer within {policy.BusyTimeout.TotalSeconds:0.##} s (quickbooks-busy); the request may have been applied");
        }
        finally
        {
            if (task is null || task.IsCompleted)
            {
                cts.Dispose();
                _lock.Release();
            }
            else
            {
                // Abandoned: keep the lock until the call really ends, so QuickBooks never sees two sessions from this app.
                cts.Cancel();
                _ = task.ContinueWith(
                    t =>
                    {
                        _ = t.Exception;
                        cts.Dispose();
                        _lock.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
