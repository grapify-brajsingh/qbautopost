using Serilog.Core;
using Serilog.Events;

namespace QbAutopost.Api.Logging;

/// <summary>
/// Spec §14 correlation id: every line carries <c>jobId</c>. The job worker sets it as a scope; a line that
/// names a job in its message (<c>{JobId}</c>) gets that value; any other line gets <see cref="None"/>.
/// </summary>
public sealed class JobIdEnricher : ILogEventEnricher
{
    public const string PropertyName = "jobId";
    public const string None = "-";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.ContainsKey(PropertyName))
        {
            return;
        }

        var value = logEvent.Properties.TryGetValue("JobId", out var named) && named is ScalarValue { Value: string id }
            ? id
            : None;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, value));
    }
}
