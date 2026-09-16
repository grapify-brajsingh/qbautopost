using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.Parsing;

namespace QbAutopost.Api.Logging;

/// <summary>
/// Spec §14 / CLAUDE.md rule 6: removes secrets from a log event before any sink sees it. Three layers:
/// the configured secret values anywhere in text, the value after a secret-looking label (<c>X-Api-Key: …</c>,
/// <c>Authorization: Bearer …</c>, <c>ApiKey=…</c>), and every property whose name looks like a secret.
/// SPEC-GAP T-702: values shorter than <see cref="MinSecretLength"/> are not replaced by value (a 3-letter key would
/// garble ordinary words); they are still covered by the label and property-name rules.
/// </summary>
public sealed partial class SecretScrubber
{
    public const string Mask = "***";
    public const int MinSecretLength = 4;

    private static readonly MessageTemplateParser Parser = new();

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Api-Key", "ApiKey", "Api-Key", "Api_Key", "Authorization", "ProxyAuthorization", "Proxy-Authorization",
        "API_SERVER_KEY", "Password", "Secret", "Token", "AccessToken", "BearerToken",
    };

    private readonly string[] _secrets;

    public SecretScrubber(IEnumerable<string?> secrets)
    {
        // Longest first, so a secret that contains another is masked whole.
        _secrets = secrets
            .Where(s => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= MinSecretLength)
            .Select(s => s!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    public static bool IsSecretName(string name) =>
        SecretNames.Contains(name) || name.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Password", StringComparison.OrdinalIgnoreCase);

    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = BearerValue().Replace(text, "${label}" + Mask);
        return LabelledValue().Replace(text, "${label}" + Mask);
    }

    /// <summary>The same event with secrets masked; the original instance when nothing had to change.</summary>
    public LogEvent Scrub(LogEvent e)
    {
        var changed = false;

        var templateText = Scrub(e.MessageTemplate.Text);
        var template = e.MessageTemplate;
        if (!string.Equals(templateText, template.Text, StringComparison.Ordinal))
        {
            template = Parser.Parse(templateText);
            changed = true;
        }

        var properties = new List<LogEventProperty>(e.Properties.Count);
        foreach (var (name, value) in e.Properties)
        {
            var scrubbed = IsSecretName(name) ? new ScalarValue(Mask) : ScrubValue(value);
            changed |= !ReferenceEquals(scrubbed, value);
            properties.Add(new LogEventProperty(name, scrubbed));
        }

        var exception = e.Exception;
        if (exception is not null)
        {
            var text = exception.ToString();
            var clean = Scrub(text);
            if (!string.Equals(text, clean, StringComparison.Ordinal))
            {
                exception = new ScrubbedException(exception, Scrub(exception.Message), clean);
                changed = true;
            }
        }

        return changed
            ? new LogEvent(e.Timestamp, e.Level, exception, template, properties, e.TraceId ?? default, e.SpanId ?? default)
            : e;
    }

    private LogEventPropertyValue ScrubValue(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string s }:
                var clean = Scrub(s);
                return string.Equals(clean, s, StringComparison.Ordinal) ? value : new ScalarValue(clean);

            case StructureValue structure:
                var changed = false;
                var props = structure.Properties.Select(p =>
                {
                    var v = IsSecretName(p.Name) ? new ScalarValue(Mask) : ScrubValue(p.Value);
                    changed |= !ReferenceEquals(v, p.Value);
                    return new LogEventProperty(p.Name, v);
                }).ToList();
                return changed ? new StructureValue(props, structure.TypeTag) : value;

            case SequenceValue sequence:
                var items = sequence.Elements.Select(ScrubValue).ToList();
                return items.Where((v, i) => !ReferenceEquals(v, sequence.Elements[i])).Any() ? new SequenceValue(items) : value;

            case DictionaryValue dictionary:
                var dictChanged = false;
                var entries = dictionary.Elements.Select(kv =>
                {
                    var v = kv.Key.Value is string key && IsSecretName(key) ? new ScalarValue(Mask) : ScrubValue(kv.Value);
                    dictChanged |= !ReferenceEquals(v, kv.Value);
                    return new KeyValuePair<ScalarValue, LogEventPropertyValue>(kv.Key, v);
                }).ToList();
                return dictChanged ? new DictionaryValue(entries) : value;

            default:
                return value;
        }
    }

    [GeneratedRegex(@"(?<label>\bBearer\s+)[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerValue();

    [GeneratedRegex(@"(?<label>\b(?:x-api-key|api[-_]?key|authorization|api_server_key|password)[""']?\s*[:=]\s*[""']?)(?!Bearer\s)(?!\*\*\*)[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledValue();
}

/// <summary>Stands in for an exception whose text contained a secret; keeps the type name for the reader.</summary>
public sealed class ScrubbedException(Exception original, string message, string text) : Exception(message)
{
    public string OriginalType { get; } = original.GetType().FullName ?? original.GetType().Name;

    public override string ToString() => text;
}
