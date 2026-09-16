using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// Hermes T4 answer for one line (spec §9.4). <see cref="Validate"/> checks the shape (account present,
/// 0 ≤ confidence ≤ 1); <see cref="MustBeOneOf"/> adds the list check, which needs the job's accounts.
/// </summary>
public sealed record AccountAnswer : IValidatable
{
    public string? Account { get; init; }
    public double? Confidence { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string?>? Alternatives { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Account))
        {
            errors.Add("account is empty; it must be one of the listed accounts.");
        }

        if (Confidence is not { } confidence)
        {
            errors.Add("confidence is missing; it must be a number from 0 to 1.");
        }
        else if (confidence is < 0 or > 1 || double.IsNaN(confidence))
        {
            errors.Add($"confidence is {confidence}; it must be a number from 0 to 1.");
        }

        return errors;
    }

    /// <summary>
    /// §9.4: the account must be one of <paramref name="accounts"/>, compared case-sensitively and exactly (no trimming),
    /// so the name sent to QuickBooks is always a listed one. Alternatives are not checked; unlisted ones are dropped later.
    /// </summary>
    public static Func<IValidatable, IReadOnlyList<string>> MustBeOneOf(IReadOnlyList<string> accounts)
    {
        var allowed = accounts.ToHashSet(StringComparer.Ordinal);
        return answer =>
        {
            var account = (answer as AccountAnswer)?.Account;
            return account is not null && allowed.Contains(account)
                ? []
                : [$"account is \"{account}\"; it must be exactly one of the listed accounts, spelled and capitalised as listed."];
        };
    }
}
