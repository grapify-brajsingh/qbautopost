using System.Globalization;
using System.Text.RegularExpressions;

namespace QbAutopost.Api.SavedRequests;

/// <summary>
/// Saved-requests spec 6: <c>{id}</c> is the numeric id, or the public reference <c>SR-000123</c> for the same record.
/// Anything else (a sign, a decimal point, a lower-case prefix, more than 18 digits, zero) is not an id, and the route
/// answers 404 rather than guessing what was meant.
/// </summary>
public static partial class SavedRequestId
{
    public static bool TryParse(string? text, out long id)
    {
        id = 0;
        if (text is null)
        {
            return false;
        }

        var match = Pattern().Match(text);
        return match.Success
               && long.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
               && id >= 1;
    }

    // 18 digits always fit in a long, so a valid-looking id can never overflow into an exception.
    [GeneratedRegex(@"^(?:SR-)?(?<n>[0-9]{1,18})\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
