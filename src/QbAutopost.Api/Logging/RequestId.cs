using System.Security.Cryptography;

namespace QbAutopost.Api.Logging;

/// <summary>
/// api-v1 §2.6: the per-request correlation id. 26 characters, sortable by time and URL-safe, so it can be echoed in
/// a header, quoted in a problem detail, grepped out of the log and read in order out of the audit file (FR-A-16).
/// <para>
/// The shape is a ULID: 10 characters of millisecond timestamp followed by 16 of randomness, in Crockford's base32.
/// The alphabet ascends in ASCII order, so an ordinal sort is a time sort — which is what makes the audit file
/// readable without parsing every line. An inbound <c>X-Request-Id</c> is honoured when it matches
/// <c>^[A-Za-z0-9_-]{8,64}$</c>, so a caller's own tracing id survives into our evidence; anything else is replaced
/// rather than repaired, because a header a stranger controls must never reach a file or a log line unexamined.
/// </para>
/// </summary>
public static class RequestId
{
    public const string Header = "X-Request-Id";

    /// <summary>The log property and the audit field; lower-case because it is a wire name (api-v1 §2.5).</summary>
    public const string PropertyName = "requestId";

    /// <summary>What a log line outside any request carries, as <c>jobId</c> does.</summary>
    public const string None = "-";

    public const int Length = 26;

    private const int TimeLength = 10;

    private const int MinInbound = 8;

    private const int MaxInbound = 64;

    /// <summary>Crockford's base32: no I, L, O or U, and ascending in ASCII so ordinal order is time order.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly string ItemKey = typeof(RequestId).FullName!;

    /// <summary>api-v1 §2.6's pattern, hand-rolled: a regex here would be a dependency on one line of validation.</summary>
    public static bool IsAcceptable(string? value)
    {
        if (value is null || value.Length is < MinInbound or > MaxInbound)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A fresh id whose first ten characters are <paramref name="utc"/> to the millisecond.</summary>
    public static string New(DateTime utc)
    {
        var ms = (long)(utc.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        if (ms < 0)
        {
            // A clock before 1970 is not worth a branch anywhere else; it must not produce a shorter id.
            ms = 0;
        }

        Span<char> chars = stackalloc char[Length];
        for (var i = TimeLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ms & 31)];
            ms >>= 5;
        }

        // One byte per character: 256 is a multiple of 32, so masking the low five bits stays uniform.
        Span<byte> random = stackalloc byte[Length - TimeLength];
        RandomNumberGenerator.Fill(random);
        for (var i = 0; i < random.Length; i++)
        {
            chars[TimeLength + i] = Alphabet[random[i] & 31];
        }

        return new string(chars);
    }

    /// <summary>The caller's id when it is acceptable, otherwise a fresh one.</summary>
    public static string Choose(string? inbound, DateTime utc) =>
        IsAcceptable(inbound) ? inbound! : New(utc);

    /// <summary>The id this request was given, or null before <see cref="RequestIdMiddleware"/> has run.</summary>
    public static string? Of(HttpContext context) => context.Items[ItemKey] as string;

    internal static void Set(HttpContext context, string id) => context.Items[ItemKey] = id;
}
