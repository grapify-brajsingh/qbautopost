using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace QbAutopost.Api.Logging;

/// <summary>
/// Spec §14 logging: Serilog to the console and to a daily rolling file under <c>Paths:Logs</c>, one <c>jobId</c> on
/// every line, secrets masked before any sink. Levels come from <c>Serilog:MinimumLevel:Default</c> and
/// <c>Serilog:MinimumLevel:Override:&lt;category&gt;</c>. SPEC-GAP T-702: sinks are fixed in code on purpose, so a sink
/// added in configuration cannot bypass the scrubber.
/// </summary>
public static class LoggingSetup
{
    public const string FilePrefix = "qbautopost-";
    public const int RetainedDays = 31;

    /// <summary>
    /// T-912 (api-v1 §8): <c>jobId</c> keeps its own column — a line about a job is grepped by <c>[jobId]</c> — and
    /// <c>requestId</c> and <c>clientId</c> get theirs beside it, so "which call did this" and "whose call was it"
    /// are answerable from the text file rather than only from a structured sink nobody has configured.
    /// </summary>
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{jobId}] [{requestId}] [{clientId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private static readonly Dictionary<string, LogEventLevel> DefaultOverrides = new(StringComparer.Ordinal)
    {
        ["Microsoft"] = LogEventLevel.Warning,
        ["Microsoft.Hosting.Lifetime"] = LogEventLevel.Information,
        // Request lines carry URLs only, but they are noise at Information and HttpClient may log headers at lower levels.
        ["System.Net.Http.HttpClient"] = LogEventLevel.Warning,
    };

    public static WebApplicationBuilder AddQbAutopostLogging(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog(
            (context, services, config) => Configure(config, context.Configuration, services.GetRequiredService<IOptions<AppSettings>>().Value),
            preserveStaticLogger: true);
        return builder;
    }

    public static void Configure(LoggerConfiguration config, IConfiguration configuration, AppSettings settings)
    {
        var levels = configuration.GetSection("Serilog:MinimumLevel");
        config.MinimumLevel.Is(ParseLevel(levels["Default"], LogEventLevel.Information));
        var overrides = new Dictionary<string, LogEventLevel>(DefaultOverrides, StringComparer.Ordinal);
        foreach (var entry in levels.GetSection("Override").GetChildren())
        {
            overrides[entry.Key] = ParseLevel(entry.Value, LogEventLevel.Information);
        }

        foreach (var (category, level) in overrides)
        {
            config.MinimumLevel.Override(category, level);
        }

        var scrubber = new SecretScrubber(SecretValues(configuration, settings));
        config.Enrich.FromLogContext()
            .Enrich.With<JobIdEnricher>()
            .Enrich.With<RequestContextEnricher>();

        LoggerSinkConfiguration.Wrap(
            config.WriteTo,
            sink => new ScrubbingSink(sink, scrubber),
            sinks =>
            {
                sinks.Console(outputTemplate: OutputTemplate, formatProvider: System.Globalization.CultureInfo.InvariantCulture);
                sinks.File(
                    Path.Combine(settings.Paths.Logs, FilePrefix + ".log"),
                    outputTemplate: OutputTemplate,
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedDays,
                    rollOnFileSizeLimit: true,
                    shared: true);
            });
    }

    /// <summary>Configured keys, plus every configuration value whose key looks like a secret (e.g. a provider key from the environment).</summary>
    public static IEnumerable<string?> SecretValues(IConfiguration configuration, AppSettings settings) =>
        new[] { settings.Api.ApiKey, settings.Hermes.ApiKey }
            .Concat(configuration.AsEnumerable()
                .Where(kv => SecretScrubber.IsSecretName(kv.Key.Split(':')[^1]))
                .Select(kv => kv.Value));

    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level) ? level : fallback;
}
