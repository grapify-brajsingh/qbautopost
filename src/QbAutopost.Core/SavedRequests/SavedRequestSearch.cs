namespace QbAutopost.Core.SavedRequests;

/// <summary>The v2 <c>q</c> rule for push ids (spec saved-requests-dashboard-v2 5.2), shared by both stores.</summary>
public static class SavedRequestSearch
{
    /// <summary>
    /// <paramref name="q"/> with its dashes removed, lower-cased, when that is 1-32 hex digits: the prefix to match
    /// against <c>push_id_hex</c>. Otherwise null (no push id search).
    /// </summary>
    public static string? PushIdHexPrefix(string q)
    {
        var hex = q.Replace("-", "", StringComparison.Ordinal);
        return hex.Length is >= 1 and <= 32 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
    }
}
