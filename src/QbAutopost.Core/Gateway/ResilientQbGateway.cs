using QbAutopost.Core.Abstractions;
using QbAutopost.Core.QbXml;

namespace QbAutopost.Core.Gateway;

/// <summary>FR-11 timing settings (<c>QuickBooks:BusyTimeoutSeconds</c>; the retry delay is 5 s by spec).</summary>
public sealed record QbGatewayPolicy
{
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Wraps the SDK gateway (FR-11):
/// <list type="bullet">
/// <item>one call at a time for the whole process (the worker, undo, sync-lists and health share it);</item>
/// <item>a call that runs longer than <see cref="QbGatewayPolicy.BusyTimeout"/> is abandoned with
/// <see cref="QuickBooksBusyException"/>. A COM call cannot be cancelled, so an abandoned call keeps the lock until it
/// really ends; a caller that cannot get the lock within the busy timeout gets <see cref="QuickBooksUnavailableException"/>
/// (nothing was sent);</item>
/// <item>a failed call is retried once after <see cref="QbGatewayPolicy.RetryDelay"/> when repeating it cannot post
/// twice: the session could not open (nothing sent), or a COM error hit a read-only message set.</item>
/// </list>
/// </summary>
public sealed class ResilientQbGateway(
    IQbGateway inner, QbGatewayPolicy policy, Func<TimeSpan, CancellationToken, Task>? delay = null) : IQbGateway
{
    private const string LockTimeoutKey = "qbautopost-lock-timeout";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public IQbGateway Inner => inner;

    /// <summary>The ordinary per-call budget (FR-11). A call that outlives it is worth explaining (FR-A-4).</summary>
    public TimeSpan BusyTimeout => policy.BusyTimeout;

    /// <summary>
    /// Raised after the process-wide lock is taken, with how long the caller waited. T-904: the log was silent while a
    /// request queued behind another QuickBooks call, so a slow answer looked like a slow QuickBooks.
    /// </summary>
    public event Action<TimeSpan>? Waited;

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
    {
        var readOnly = QbXmlRequests.IsReadOnly(qbxml);
        return RunWithRetryAsync(token => inner.ProcessAsync(qbxml, token), readOnly, ct, timeout: null);
    }

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        RunWithRetryAsync(inner.CurrentCompanyFileAsync, readOnly: true, ct, timeout: null);

    /// <summary>
    /// One read-only call with its own timeout instead of <see cref="QbGatewayPolicy.BusyTimeout"/> (FR-A-4). The
    /// first call of the day can then outlast the certificate dialog — 101 s on the owner's server — which the 60 s
    /// busy timeout abandoned even though the session itself succeeded. Never used for a call that writes.
    /// </summary>
    public Task<string> ProcessWithTimeoutAsync(string qbxml, TimeSpan timeout, CancellationToken ct)
    {
        if (!QbXmlRequests.IsReadOnly(qbxml))
        {
            throw new InvalidOperationException("A longer timeout is only for read-only requests; a write keeps the busy timeout (FR-11).");
        }

        return RunWithRetryAsync(token => inner.ProcessAsync(qbxml, token), readOnly: true, ct, timeout);
    }

    /// <inheritdoc cref="ProcessWithTimeoutAsync"/>
    public Task<string> CurrentCompanyFileAsync(TimeSpan timeout, CancellationToken ct) =>
        RunWithRetryAsync(inner.CurrentCompanyFileAsync, readOnly: true, ct, timeout);

    // SPEC-GAP T-604: FR-11 retries "a COMException". A COM error from ProcessRequest on an add or delete may already
    // have been applied, so only failures that cannot post twice are retried.
    private static bool CanRetry(Exception ex, bool readOnly) =>
        ex switch
        {
            QuickBooksUnavailableException when ex.Data.Contains(LockTimeoutKey) => false,
            QuickBooksUnavailableException => true,
            QuickBooksCallException => readOnly,
            _ => false,
        };

    private async Task<string> RunWithRetryAsync(Func<CancellationToken, Task<string>> call, bool readOnly, CancellationToken ct, TimeSpan? timeout)
    {
        try
        {
            return await RunExclusiveAsync(call, ct, timeout);
        }
        catch (Exception ex) when (CanRetry(ex, readOnly))
        {
            await _delay(policy.RetryDelay, ct);
            return await RunExclusiveAsync(call, ct, timeout);
        }
    }

    private async Task<string> RunExclusiveAsync(Func<CancellationToken, Task<string>> call, CancellationToken ct, TimeSpan? timeout)
    {
        // The wait for the lock and the call itself get the same budget: a caller asking for longer means "I am
        // willing to wait", whether the delay is another call ahead of it or QuickBooks itself.
        var limit = timeout ?? policy.BusyTimeout;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        if (!await _lock.WaitAsync(limit, ct))
        {
            var busy = new QuickBooksUnavailableException("QuickBooks is still busy with an earlier call that did not finish");
            busy.Data[LockTimeoutKey] = true;
            throw busy;
        }

        waited.Stop();
        Waited?.Invoke(waited.Elapsed);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<string>? task = null;
        try
        {
            task = call(cts.Token);
            return await task.WaitAsync(limit, ct);
        }
        catch (TimeoutException) when (task is { IsCompleted: false })
        {
            throw new QuickBooksBusyException(
                $"QuickBooks did not answer within {limit.TotalSeconds:0.##} s (quickbooks-busy); the request may have been applied");
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
