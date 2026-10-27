using System.Diagnostics;
using Npgsql;
using QbAutopost.Data;

namespace QbAutopost.Api.Database;

/// <summary>
/// The one place that knows whether the database feature may be used (spec 5.5, 10.6, 10.7).
/// <para>
/// It checks the schema once at start, and again only when something asks: a store call that throws
/// <see cref="NpgsqlException"/> or <see cref="TimeoutException"/> (see <see cref="IsDatabaseFailure"/>), an explicit
/// <see cref="RefreshAsync"/>, or a request that finds the gate not ready with a check older than
/// <see cref="StaleAfter"/> (<see cref="RefreshSharedAsync"/>). There is no timer (CLAUDE.md).
/// </para>
/// <para>
/// The gate reports Ready only after in-flight recovery (spec 10.6) has succeeded once in this process: recovery is a
/// step of the refresh that would make the gate Ready, so no route can claim a request before every stale
/// <c>posting</c> row has been moved to <c>unknown</c>. If recovery throws the gate stays not ready and the next
/// refresh tries again.
/// </para>
/// </summary>
public sealed class DatabaseGate
{
    public const string ReasonNotConfigured = "not-configured";

    /// <summary>A not-ready gate is looked at again by a request once its last check is older than this.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<SchemaStatus>>? _check;
    private readonly Func<CancellationToken, Task>? _recover;
    private readonly bool _requireCurrentSchema;
    private readonly ILogger<DatabaseGate> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly object _sync = new();
    private Task? _shared;
    private volatile DatabaseState _state;
    private volatile string? _reason;
    private volatile IReadOnlyList<string> _pending = [];
    private long _latencyMs = -1;
    private long _lastCheckedTicks;
    private volatile bool _checked;
    private volatile bool _recovered;

    /// <param name="check">Null when no connection string is configured.</param>
    /// <param name="recover">Runs once per process, before the first Ready; null when there is nothing to recover.</param>
    public DatabaseGate(
        Func<CancellationToken, Task<SchemaStatus>>? check,
        bool requireCurrentSchema,
        ILogger<DatabaseGate> log,
        Func<CancellationToken, Task>? recover = null,
        Func<DateTime>? utcNow = null)
    {
        _check = check;
        _recover = recover;
        _requireCurrentSchema = requireCurrentSchema;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _state = check is null ? DatabaseState.NotConfigured : DatabaseState.Unreachable;
        _reason = check is null ? ReasonNotConfigured : SchemaState.Unreachable;
        _checked = check is null;
    }

    public DatabaseState State => _state;

    public bool IsReady => _state == DatabaseState.Ready;

    /// <summary>Why the gate is not ready: <c>not-configured</c>, <c>unreachable</c> or <c>schema-out-of-date</c>; null when ready.</summary>
    public string? Reason => _state == DatabaseState.Ready ? null : _reason;

    /// <summary>Migrations this build carries that the database lacks, as of the last check.</summary>
    public IReadOnlyList<string> Pending => _pending;

    /// <summary>Round trip of the last check in milliseconds; -1 when none ran.</summary>
    public long LatencyMs => Interlocked.Read(ref _latencyMs);

    /// <summary>True once a check ran, <see cref="Set"/> was called, or nothing is configured.</summary>
    public bool Checked => _checked;

    /// <summary>True when a check is configured, the gate is not ready, and its last check is older than <see cref="StaleAfter"/>.</summary>
    public bool NeedsLookAgain =>
        _check is not null
        && !IsReady
        && (!_checked || _utcNow() - new DateTime(Interlocked.Read(ref _lastCheckedTicks), DateTimeKind.Utc) > StaleAfter);

    /// <summary>Checks the database now (and recovers in-flight requests first, once). A no-op when nothing is configured.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (_check is null)
        {
            return;
        }

        await _refreshing.WaitAsync(ct);
        try
        {
            var timer = Stopwatch.StartNew();
            SchemaStatus status;
            try
            {
                status = await _check(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception text can name the host; log the type only.
                _log.LogWarning("Database check failed: {ExceptionType}", ex.GetType().Name);
                status = new SchemaStatus(false, false, [], SchemaState.Unreachable);
            }

            Interlocked.Exchange(ref _latencyMs, timer.ElapsedMilliseconds);
            _pending = status.Pending;
            var next = !status.Reachable
                ? DatabaseState.Unreachable
                : status.Current || !_requireCurrentSchema ? DatabaseState.Ready : DatabaseState.SchemaOutOfDate;
            var reason = status.Reason;

            if (next == DatabaseState.Ready && !_recovered && _recover is not null)
            {
                try
                {
                    await _recover(ct);
                    _recovered = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Type only: an Npgsql message can name the host. Not ready until recovery has succeeded.
                    _log.LogError("Saved-request recovery failed: {ExceptionType}; the database stays not ready", ex.GetType().Name);
                    next = DatabaseState.Unreachable;
                    reason = SchemaState.Unreachable;
                }
            }

            Apply(next, reason);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// A refresh that concurrent callers share: while one is in flight everyone awaits it, so N requests arriving
    /// during an outage cost one check, not N. The caller's cancellation stops its wait, not the shared check.
    /// </summary>
    public Task RefreshSharedAsync(CancellationToken ct)
    {
        Task shared;
        lock (_sync)
        {
            if (_shared is not { IsCompleted: false })
            {
                _shared = Task.Run(() => RefreshAsync(CancellationToken.None), CancellationToken.None);
            }

            shared = _shared;
        }

        return shared.WaitAsync(ct);
    }

    /// <summary>Forces a state without checking; for tests.</summary>
    public void Set(DatabaseState state) =>
        Apply(state, state switch
        {
            DatabaseState.NotConfigured => ReasonNotConfigured,
            DatabaseState.Unreachable => SchemaState.Unreachable,
            DatabaseState.SchemaOutOfDate => SchemaState.OutOfDate,
            _ => null,
        });

    /// <summary>True when <paramref name="ex"/>, or anything it wraps, says the database could not be reached.</summary>
    public static bool IsDatabaseFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is NpgsqlException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private void Apply(DatabaseState next, string? reason)
    {
        var previous = _state;
        _reason = reason;
        _state = next;
        _checked = true;
        Interlocked.Exchange(ref _lastCheckedTicks, _utcNow().Ticks);
        if (previous != next)
        {
            _log.LogInformation("Database state {Previous} -> {State}", previous, next);
        }
    }
}
