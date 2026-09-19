using Microsoft.Extensions.Logging;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>One captured log call: level, rendered message and exception.</summary>
public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>An <see cref="ILogger{T}"/> that keeps every entry in memory, for tests of classes that log.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public string Text => string.Join('\n', Entries.Select(e => $"{e.Level}: {e.Message}"));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
