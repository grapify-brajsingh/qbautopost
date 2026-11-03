using Microsoft.Extensions.Options;

namespace QbAutopost.Api.Configuration;

/// <summary>M17 §4.1: <c>Companies</c> values the app cannot honour stop startup with the key named.</summary>
public sealed class CompaniesOptionsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        var c = options.Companies;
        var failures = new List<string>();
        if (!ScheduledSyncModes.IsKnown(c.ScheduledSync))
        {
            failures.Add($"Companies:ScheduledSync must be '{ScheduledSyncModes.WaitingOnly}', '{ScheduledSyncModes.All}' or '{ScheduledSyncModes.Off}', not '{c.ScheduledSync}'.");
        }

        if (c.Configured)
        {
            if (!Path.IsPathFullyQualified(c.RootFolder))
            {
                failures.Add($"Companies:RootFolder must be an absolute path, not '{c.RootFolder}'.");
            }

            if (!File.Exists(c.RulesTemplate))
            {
                failures.Add($"Companies:RulesTemplate '{c.RulesTemplate}' does not exist; it is copied for every company found in Companies:RootFolder.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
