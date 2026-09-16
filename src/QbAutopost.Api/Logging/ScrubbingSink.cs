using Serilog.Core;
using Serilog.Events;

namespace QbAutopost.Api.Logging;

/// <summary>Passes every event through <see cref="SecretScrubber"/> before the wrapped sinks (console, file) write it.</summary>
public sealed class ScrubbingSink(ILogEventSink inner, SecretScrubber scrubber) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent) => inner.Emit(scrubber.Scrub(logEvent));

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
