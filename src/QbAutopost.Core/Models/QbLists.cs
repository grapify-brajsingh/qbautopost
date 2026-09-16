namespace QbAutopost.Core.Models;

/// <summary>Cached QuickBooks name lists (<c>qb-lists.json</c>, spec §7, FR-15).</summary>
public sealed record QbLists
{
    public DateTime? SyncedUtc { get; init; }
    public IReadOnlyList<QbAccount> Accounts { get; init; } = [];
    public IReadOnlyList<string> Vendors { get; init; } = [];
    public IReadOnlyList<string> Customers { get; init; } = [];

    public static QbLists Empty { get; } = new();
}

public sealed record QbAccount
{
    public required string Name { get; init; }

    /// <summary>QuickBooks AccountType (e.g. <c>Expense</c>); null when unknown.</summary>
    public string? Type { get; init; }
}
