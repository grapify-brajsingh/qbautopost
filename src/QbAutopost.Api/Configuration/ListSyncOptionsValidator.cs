using Microsoft.Extensions.Options;

namespace QbAutopost.Api.Configuration;

/// <summary>M14 §11: <c>QuickBooks:ListSync</c> values the schedule cannot honour stop startup with the key named.</summary>
public sealed class ListSyncOptionsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        var s = options.QuickBooks.ListSync;
        var failures = new List<string>();
        if (s.IntervalMinutes is not 0 && s.IntervalMinutes < ListSyncSettings.MinIntervalMinutes)
        {
            failures.Add($"QuickBooks:ListSync:IntervalMinutes must be 0 (schedule off) or at least {ListSyncSettings.MinIntervalMinutes}, not {s.IntervalMinutes}.");
        }

        else if (s.IntervalMinutes > ListSyncSettings.MaxMinutes)
        {
            failures.Add($"QuickBooks:ListSync:IntervalMinutes must be at most {ListSyncSettings.MaxMinutes} (one day), not {s.IntervalMinutes}.");
        }

        if (s.FirstDelayMinutes < 0)
        {
            failures.Add($"QuickBooks:ListSync:FirstDelayMinutes must be 0 or more, not {s.FirstDelayMinutes}.");
        }
        else if (s.FirstDelayMinutes > ListSyncSettings.MaxMinutes)
        {
            failures.Add($"QuickBooks:ListSync:FirstDelayMinutes must be at most {ListSyncSettings.MaxMinutes} (one day), not {s.FirstDelayMinutes}.");
        }

        if (s.HistoryRows < ListSyncSettings.MinHistoryRows)
        {
            failures.Add($"QuickBooks:ListSync:HistoryRows must be at least {ListSyncSettings.MinHistoryRows}, not {s.HistoryRows}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
