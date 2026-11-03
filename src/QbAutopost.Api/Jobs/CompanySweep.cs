using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Companies;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Api.Jobs;

/// <summary>M17 §4.1, §4.7, §6 d: which companies a scheduled sweep syncs.</summary>
public static class CompanySweep
{
    /// <summary>
    /// Only <c>active</c> + <c>approved</c> companies, in id order. <c>waiting-only</c>: those with pending statements
    /// or never synced (no attempt of any outcome); <c>all</c>: every one; <c>off</c> (or anything else): none.
    /// </summary>
    public static IReadOnlyList<Company> Select(
        IReadOnlyList<Company> companies,
        string mode,
        IReadOnlyDictionary<int, int> pendingByCompany,
        Func<int, ListSyncRun?> lastAttempt)
    {
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(pendingByCompany);
        ArgumentNullException.ThrowIfNull(lastAttempt);

        var ready = companies.Where(c => c.CanOpenSession).OrderBy(c => c.Id);
        return mode switch
        {
            ScheduledSyncModes.All => ready.ToList(),
            ScheduledSyncModes.WaitingOnly => ready
                .Where(c => pendingByCompany.GetValueOrDefault(c.Id) > 0 || lastAttempt(c.Id) is null)
                .ToList(),
            _ => [],
        };
    }
}

/// <summary>
/// What <see cref="JobWorker"/>'s scheduled attempt reads when there are several companies (M17 §4.7): whether to
/// sweep at all, and the contexts of the companies selected now. A context that may not open a session (its file is
/// outside the root, ruling B14) is dropped as well.
/// </summary>
public sealed class CompanySweepSource(
    ICompanyResolver resolver,
    IQbListsCatalog catalog,
    IOptions<AppSettings> settings,
    ISavedRequestV2Store requests,
    ICompanyStore? companies = null)
{
    /// <summary>True with <c>Companies:RootFolder</c> and a company store; otherwise the worker syncs the default company as before M17.</summary>
    public bool Enabled => resolver.MultiCompany && companies is not null;

    public string Mode => settings.Value.Companies.ScheduledSync;

    /// <summary>Several companies and <c>Companies:ScheduledSync</c> = <c>off</c>: no scheduled sync at all (no slot, no read).</summary>
    public bool Off => Enabled && Mode == ScheduledSyncModes.Off;

    /// <summary>
    /// The company as it is now, for a sweep step: the snapshot taken when the sweep began can be stale (the company was
    /// disabled, re-pointed or lost its approval since). Null when it may no longer open a session; its lists must not be
    /// recorded under a file that was not the one chosen.
    /// </summary>
    public async Task<CompanyContext?> ReResolveAsync(CompanyContext chosen, CancellationToken ct) =>
        await resolver.GetAsync(chosen.Id, ct) is { CanOpenSession: true } now ? now : null;

    public async Task<IReadOnlyList<CompanyContext>> SelectAsync(CancellationToken ct)
    {
        if (companies is null)
        {
            return [];
        }

        var all = await companies.ListAsync(ct);
        var mode = Mode;
        var pending = mode == ScheduledSyncModes.WaitingOnly
            ? await requests.PendingByCompanyAsync(ct)
            : new Dictionary<int, int>();
        var contexts = new List<CompanyContext>();
        foreach (var company in CompanySweep.Select(all, mode, pending, id => catalog.SourceFor(id).LastAttempt))
        {
            if (await resolver.GetAsync(company.Id, ct) is { CanOpenSession: true } context)
            {
                contexts.Add(context);
            }
        }

        return contexts;
    }
}
