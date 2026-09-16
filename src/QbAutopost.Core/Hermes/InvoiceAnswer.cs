using System.Globalization;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// Hermes T3 answer for one invoice file (spec §9.3). Validation: a party, role vendor or customer, total &gt; 0 with at
/// most 2 decimals, and an ISO date when one is given. The total is copied from the invoice, never computed.
/// </summary>
public sealed record InvoiceAnswer : IValidatable
{
    public string? Party { get; init; }
    public string? Role { get; init; }
    public string? Number { get; init; }
    public string? Date { get; init; }
    public decimal? Total { get; init; }
    public string? CategoryHint { get; init; }

    public PartyRole PartyRole => ParseRole(Role) ?? throw new InvalidOperationException("Answer was not validated.");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Party))
        {
            errors.Add("party is empty; it must be the name of the other business on the invoice.");
        }

        if (ParseRole(Role) is null)
        {
            errors.Add($"role is \"{Role}\"; allowed values are \"vendor\" and \"customer\".");
        }

        if (Number is not null && string.IsNullOrWhiteSpace(Number))
        {
            errors.Add("number is blank; use null when the invoice shows no number.");
        }

        if (Date is not null
            && !DateOnly.TryParseExact(Date, StatementAnswer.IsoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"date is \"{Date}\"; it must be a real date written yyyy-MM-dd, or null when not shown.");
        }

        if (Total is not { } total)
        {
            errors.Add("total is missing.");
        }
        else if (total <= 0m)
        {
            errors.Add($"total is {total}; it must be greater than 0.");
        }
        else if (decimal.Round(total, 2) != total)
        {
            errors.Add($"total is {total}; money has at most 2 decimal places.");
        }

        return errors;
    }

    /// <summary>The validated answer as <see cref="InvoiceFacts"/> for <paramref name="fileName"/>.</summary>
    public InvoiceFacts ToFacts(string fileName) => new()
    {
        File = fileName,
        Party = Party!.Trim(),
        Role = PartyRole,
        Number = Number?.Trim(),
        Date = StatementAnswer.ToDate(Date),
        Total = Total!.Value,
        CategoryHint = string.IsNullOrWhiteSpace(CategoryHint) ? null : CategoryHint.Trim(),
    };

    private static PartyRole? ParseRole(string? role) => role?.ToLowerInvariant() switch
    {
        "vendor" => Models.PartyRole.Vendor,
        "customer" => Models.PartyRole.Customer,
        _ => null,
    };
}
