using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QbAutopost.Core.SavedRequests;

/// <summary>Email and push metadata read from the original body of a v2 save (spec 5.1 and 5.2).</summary>
public sealed record SaveMetadata(
    string? EmailSubject,
    DateTime? EmailReceivedUtc,
    string? EmailFrom,
    string? EmailMessageId,
    Guid? PushId,
    IReadOnlyList<string> MetadataErrors)
{
    public static SaveMetadata None { get; } = new(null, null, null, null, null, Array.Empty<string>());

    public bool HasEmail => EmailSubject is not null || EmailReceivedUtc is not null
        || EmailFrom is not null || EmailMessageId is not null;
}

/// <summary>Reads <see cref="SaveMetadata"/> from the ORIGINAL payload. Malformed email fields stay null without a flag.</summary>
public static partial class SaveMetadataReader
{
    public const int MaxTextLength = 998;

    public const string PushIdError = "$.pushId: not a UUID";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}(:?\d{2})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstantWithOffset();

    public static SaveMetadata Read(JsonElement originalPayload)
    {
        if (originalPayload.ValueKind != JsonValueKind.Object)
        {
            return SaveMetadata.None;
        }

        string? subject = null, from = null, messageId = null;
        DateTime? received = null;
        if (originalPayload.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.Object)
        {
            subject = Text(email, "subject");
            from = Text(email, "from");
            messageId = Text(email, "messageId");
            received = Instant(email, "receivedUtc");
        }

        Guid? pushId = null;
        var errors = new List<string>();
        if (originalPayload.TryGetProperty("pushId", out var push) && push.ValueKind != JsonValueKind.Null)
        {
            var text = push.ValueKind == JsonValueKind.String ? push.GetString() : null;
            if (text is not null
                && (Guid.TryParseExact(text, "D", out var d) || Guid.TryParseExact(text, "N", out d)))
            {
                pushId = d;
            }
            else
            {
                errors.Add(PushIdError);
            }
        }

        return new SaveMetadata(subject, received, from, messageId, pushId, errors);
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Truncate(v.GetString()) : null;

    private static DateTime? Instant(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = v.GetString();
        return text is not null && InstantWithOffset().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.UtcDateTime
                : null;
    }

    private static string? Truncate(string? s)
    {
        if (s is null || s.Length <= MaxTextLength)
        {
            return s;
        }

        // Never split a surrogate pair: if the last kept char is a high surrogate, drop it too.
        var cut = char.IsHighSurrogate(s[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return s[..cut];
    }
}
