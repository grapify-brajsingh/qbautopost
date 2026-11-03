using Microsoft.Extensions.Logging;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>An <see cref="ILoggerProvider"/> that keeps every line from every category, so a test can assert on the host's logs.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<(string Category, LogEntry Entry)> _entries = [];

    public IReadOnlyList<(string Category, LogEntry Entry)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._gate)
            {
                owner._entries.Add((category, new LogEntry(logLevel, formatter(state, exception), exception)));
            }
        }
    }
}
