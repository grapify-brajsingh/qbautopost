using QbAutopost.Api.Security;
using Serilog.Core;
using Serilog.Events;

namespace QbAutopost.Api.Logging;

/// <summary>
/// api-v1 §8: <c>requestId</c> and <c>clientId</c> belong on every line, and the output template has a column for
/// each. A line written outside any request — startup, the job worker, shutdown — has neither, so it gets
/// <see cref="RequestId.None"/> rather than an empty column, exactly as <see cref="JobIdEnricher"/> does for
/// <c>jobId</c>: a dash reads as "not applicable", a gap reads as "something is broken".
/// </summary>
public sealed class RequestContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(RequestId.PropertyName, RequestId.None));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(ApiKeyMiddleware.ClientIdProperty, RequestId.None));
    }
}
