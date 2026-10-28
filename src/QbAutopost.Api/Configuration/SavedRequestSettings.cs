namespace QbAutopost.Api.Configuration;

/// <summary>Saved-requests spec 4.2, the <c>SavedRequests</c> section.</summary>
public sealed class SavedRequestSettings
{
    public int MaxListPageSize { get; set; } = 100;

    public int DuplicateLookbackDays { get; set; } = 90;
}
