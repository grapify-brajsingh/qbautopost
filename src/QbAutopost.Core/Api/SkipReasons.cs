namespace QbAutopost.Core.Api;

/// <summary>Reasons a planned row is reported <c>Skipped</c> by the engine itself (not by a skip rule).</summary>
public static class SkipReasons
{
    /// <summary>The caller marked the row <c>post: false</c> (dashboard v2 §6.2).</summary>
    public const string NotToPost = "not-to-post";

    /// <summary>Every row is marked <c>post: false</c>, so nothing would be sent.</summary>
    public const string NothingToPost = "nothing-to-post";
}
