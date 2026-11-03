namespace QbAutopost.Core.Pipeline;

/// <summary>
/// M15 §3.1: one recorded list sync attempt with its id. <see cref="ListSyncRun.Error"/>,
/// <see cref="ListSyncRun.MissingInRules"/> and the stored lists are never read for the history, so they are always null
/// here; the counts are the run's own.
/// </summary>
public sealed record ListSyncAttempt(long Id, ListSyncRun Run);
