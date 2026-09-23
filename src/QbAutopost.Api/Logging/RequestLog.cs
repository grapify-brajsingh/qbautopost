using QbAutopost.Api.Endpoints;
using QbAutopost.Api.Security;
using Serilog;
using Serilog.Events;

namespace QbAutopost.Api.Logging;

/// <summary>
/// One line per HTTP request (spec §14): method, path (never the query or headers, so no key), status and duration.
/// 5xx is an error, 4xx a warning, a healthy <c>/health/*</c> poll is Debug (start-all.ps1 polls it). A route
/// <c>{id}</c> becomes the line's <c>jobId</c>.
/// </summary>
public static class RequestLog
{
    public const string MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0} ms";

    public static WebApplication UseQbAutopostRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            // With preserveStaticLogger the static Log.Logger stays silent; use the host's scrubbed logger.
            options.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
            options.MessageTemplate = MessageTemplate;
            options.GetLevel = Level;
            options.EnrichDiagnosticContext = (context, http) =>
            {
                if (http.Request.RouteValues.TryGetValue("id", out var id) && id is string value)
                {
                    context.Set(JobIdEnricher.PropertyName, value.Split('#')[0]);
                }

                // T-912 (api-v1 §8): this line is written after the key check's LogContext scope has gone, so the
                // caller has to be put on it here — otherwise the one line per request is the one without a client.
                if (ApiKeyMiddleware.ClientOf(http) is { } client)
                {
                    context.Set(ApiKeyMiddleware.ClientIdProperty, client.Id);
                }
            };
        });
        return app;
    }

    public static LogEventLevel Level(HttpContext http, double elapsedMs, Exception? ex) =>
        ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Response.StatusCode >= 400 ? LogEventLevel.Warning
        : ApiRoutes.IsHealth(http.Request.Path) ? LogEventLevel.Debug
        : LogEventLevel.Information;
}
