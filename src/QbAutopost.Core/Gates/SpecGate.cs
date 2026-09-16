using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Text;

namespace QbAutopost.Core.Gates;

public sealed record SpecGateResult(bool Ok, IReadOnlyList<string> Errors);

/// <summary>Gate G2 — the requirement matches the folder (spec FR-2). Any error fails the whole job.</summary>
public static class SpecGate
{
    public static SpecGateResult Check(
        JobSpec spec, string configuredCompany, IReadOnlyList<StatementSummary> statements, Rules rules)
    {
        var errors = new List<string>();

        // SPEC-GAP T-103: the app posts to the configured company file, so a requirement naming another company fails.
        if (string.IsNullOrWhiteSpace(configuredCompany))
        {
            errors.Add("Company.Name is not configured");
        }
        else if (spec.Company is not null
                 && !string.Equals(TextNormalizer.ForMatching(spec.Company), TextNormalizer.ForMatching(configuredCompany), StringComparison.Ordinal))
        {
            errors.Add($"requirement names company '{spec.Company}' but this server posts to '{configuredCompany}'");
        }

        if (spec.Kinds.Count == 0)
        {
            errors.Add("no transaction kinds found in the requirement");
        }

        var fileLast4 = statements
            .Where(s => s.Last4 is not null)
            .Select(s => s.Last4!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stated in spec.BankLast4.Concat(spec.CardLast4).Distinct(StringComparer.Ordinal))
        {
            if (!fileLast4.Contains(stated))
            {
                errors.Add($"account {stated} is stated in the requirement but has no statement file");
            }
        }

        foreach (var statement in statements.Where(s => s.Last4 is not null))
        {
            var last4 = statement.Last4!;
            var stated = spec.BankLast4.Contains(last4) || spec.CardLast4.Contains(last4);
            var registered = rules.BankAccounts.ContainsKey(last4) || rules.CardAccounts.ContainsKey(last4);
            if (!stated && !registered)
            {
                errors.Add($"statement {statement.File} (account {last4}) is neither stated in the requirement nor registered in rules.json");
            }
        }

        return new SpecGateResult(errors.Count == 0, errors);
    }
}
