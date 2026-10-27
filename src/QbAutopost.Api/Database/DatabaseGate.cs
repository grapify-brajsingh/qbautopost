using System.Diagnostics;
using Npgsql;
using QbAutopost.Data;

namespace QbAutopost.Api.Database;

/// <summary>
/// The one place that knows whether the database feature may be used (spec 5.5, 10.7).
/// <para>
/// It checks the schema once at start, and again only when something asks: a store call that throws
/// <see cref="NpgsqlException"/> or <see cref="TimeoutException"/> (see <see cref="IsDatabaseFailure"/>), or an
/// explicit <see cref="RefreshAsync"/>. There is no timer (CLAUDE.md). The state is a fact about the database, never
/// about the rest of the API, so nothing outside the saved-request routes consults it.
/// </para>
/// </summary>
public sealed class DatabaseGate
{
    public const string ReasonNotConfigured = "not-configured";

    private readonly Func<CancellationToken, Task<SchemaStatus>>? _check;
    private readonly bool _requireCurrentSchema;
    private readonly ILogger<DatabaseGate> _log;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private volatile DatabaseState _state;
    private volatile string? _reason;
    private volatile IReadOnlyList<string> _pending = [];
    private long _latencyMs = -1;
    private volatile bool _checked;

    /// <param name="check">Null when no connection string is configured.</param>
    public DatabaseGate(Func<CancellationToken, Task<SchemaStatus>>? check, bool requireCurrentSchema, ILogger<DatabaseGate> log)
    {
        _check = check;
        _requireCurrentSchema = requireCurrentSchema;
        _log = log;
        _state = check is null ? DatabaseState.NotConfigured : DatabaseState.Unreachable;
        _reason = check is null ? ReasonNotConfigured : SchemaState.Unreachable;
        _checked = check is null;
    }

    /// <summary>Raised when the state moves to <see cref="DatabaseState.Ready"/> from anything else.</summary>
    public event Action? BecameReady;

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

    /// <summary>Checks the database now. A no-op when nothing is configured.</summary>
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
            Apply(next, status.Reason);
        }
        finally
        {
            _refreshing.Release();
        }
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
        if (previous != next)
        {
            _log.LogInformation("Database state {Previous} -> {State}", previous, next);
        }

        if (next == DatabaseState.Ready && previous != DatabaseState.Ready)
        {
            BecameReady?.Invoke();
        }
    }
}
