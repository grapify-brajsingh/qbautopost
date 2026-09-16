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

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct)
    {
        var readOnly = QbXmlRequests.IsReadOnly(qbxml);
        return RunWithRetryAsync(token => inner.ProcessAsync(qbxml, token), readOnly, ct);
    }

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
        RunWithRetryAsync(inner.CurrentCompanyFileAsync, readOnly: true, ct);

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

    private async Task<string> RunWithRetryAsync(Func<CancellationToken, Task<string>> call, bool readOnly, CancellationToken ct)
    {
        try
        {
            return await RunExclusiveAsync(call, ct);
        }
        catch (Exception ex) when (CanRetry(ex, readOnly))
        {
            await _delay(policy.RetryDelay, ct);
            return await RunExclusiveAsync(call, ct);
        }
    }

    private async Task<string> RunExclusiveAsync(Func<CancellationToken, Task<string>> call, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(policy.BusyTimeout, ct))
        {
            var busy = new QuickBooksUnavailableException("QuickBooks is still busy with an earlier call that did not finish");
            busy.Data[LockTimeoutKey] = true;
            throw busy;
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
