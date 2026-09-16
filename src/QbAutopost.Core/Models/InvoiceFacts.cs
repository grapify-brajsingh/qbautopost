namespace QbAutopost.Core.Models;

public enum PartyRole
{
    Vendor,
    Customer,
}

/// <summary>Facts read from one invoice file (spec §7, §9.3). Evidence only — never posted (Q1).</summary>
public sealed record InvoiceFacts
{
    public required string File { get; init; }
    public required string Party { get; init; }
    public required PartyRole Role { get; init; }
    public string? Number { get; init; }
    public DateOnly? Date { get; init; }
    public required decimal Total { get; init; }
    public string? CategoryHint { get; init; }
    public string? MatchedRequestId { get; init; }
}
