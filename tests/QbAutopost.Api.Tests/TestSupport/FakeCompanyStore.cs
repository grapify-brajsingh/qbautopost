using QbAutopost.Core.Companies;
using QbAutopost.Core.SavedRequests.Audit;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="ICompanyStore"/>, lock-guarded, with the same rules as <c>EfCompanyStore</c> (the contract in
/// <c>CompanyStoreContract</c> pins both). <see cref="AuditEvents"/> records what the real store writes to user_audit.
/// </summary>
public sealed class FakeCompanyStore : ICompanyStore
{
    private readonly object _gate = new();
    private readonly List<Company> _companies = [];
    private readonly List<(string Kind, int? CompanyId, string Actor)> _audit = [];
    private readonly Dictionary<long, (int? CompanyId, CompanySource? Source)> _requests = [];
    private DateTime? _lastScan;
    private int _nextId = 1;
    private long _nextRequestId = 1;

    public IReadOnlyList<(string Kind, int? CompanyId, string Actor)> AuditEvents
    {
        get { lock (_gate) { return [.. _audit]; } }
    }

    /// <summary>Seeds a company as given (its Id and RowVersion are kept); returns it.</summary>
    public Company Add(Company company)
    {
        lock (_gate)
        {
            _companies.Add(company);
            _nextId = Math.Max(_nextId, company.Id + 1);
            return company;
        }
    }

    /// <summary>A stand-in for a saved request with no company, for the backfill contract.</summary>
    public long SeedUnassignedRequest()
    {
        lock (_gate)
        {
            var id = _nextRequestId++;
            _requests[id] = (null, null);
            return id;
        }
    }

    public (int? CompanyId, CompanySource? Source) RequestCompany(long requestId)
    {
        lock (_gate) { return _requests[requestId]; }
    }

    public Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct)
    {
        lock (_gate) { return Task.FromResult<IReadOnlyList<Company>>([.. _companies.OrderBy(c => c.Id)]); }
    }

    public Task<Company?> GetAsync(int id, CancellationToken ct)
    {
        lock (_gate) { return Task.FromResult(_companies.FirstOrDefault(c => c.Id == id)); }
    }

    public Task<DefaultCompanyResult> EnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_companies.Count > 0)
            {
                var existing = _companies.FirstOrDefault(c => c.IsDefault)
                    ?? throw new InvalidOperationException("The companies table has rows but none is the default company.");
                return Task.FromResult(new DefaultCompanyResult(existing, false, 0));
            }

            var created = new Company(
                _nextId++, company.FolderName, company.Code, company.DisplayName, company.FilePath, [company.FilePath],
                CompanyState.Active, CompanyAccess.Approved, nowUtc, "the configured Company:FilePath, used before M17",
                null, null, [], true, nowUtc, nowUtc, 1);
            _companies.Add(created);

            var backfilled = 0;
            foreach (var id in _requests.Where(r => r.Value.CompanyId is null).Select(r => r.Key).ToList())
            {
                _requests[id] = (created.Id, CompanySource.Migrated);
                backfilled++;
            }

            return Task.FromResult(new DefaultCompanyResult(created, true, backfilled));
        }
    }

    public Task<ScanApplied> ApplyScanAsync(ScanPlan plan, string actor, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var touched = new List<Company>();
            var added = new List<int>();
            var updated = 0;

            foreach (var change in plan.Add.Concat(plan.Update))
            {
                var index = IndexFor(change);
                if (index < 0)
                {
                    var created = new Company(
                        _nextId++, change.Folder.FolderName, change.Folder.Code, change.Folder.DisplayName, change.FilePath,
                        [.. change.Folder.Files], change.State, CompanyAccess.Unchecked, null, null, null, null, [], false,
                        nowUtc, nowUtc, 1);
                    _companies.Add(created);
                    added.Add(created.Id);
                    touched.Add(created);
                    continue;
                }

                var row = _companies[index];
                var fileChanged = !SamePath(row.FilePath, change.FilePath);
                var next = row with
                {
                    FolderName = change.Folder.FolderName,
                    Code = change.Folder.Code,
                    DisplayName = row.IsDefault ? row.DisplayName : change.Folder.DisplayName,
                    FilePath = change.FilePath,
                    CandidateFiles = [.. change.Folder.Files],
                    State = row.State == CompanyState.Disabled ? CompanyState.Disabled : change.State,
                    Access = fileChanged ? CompanyAccess.Unchecked : row.Access,
                    AccessCheckedUtc = fileChanged ? null : row.AccessCheckedUtc,
                    AccessMessage = fileChanged ? null : row.AccessMessage,
                };
                if (next with { CandidateFiles = row.CandidateFiles } == row && next.CandidateFiles.SequenceEqual(row.CandidateFiles))
                {
                    continue;
                }

                next = next with { UpdatedUtc = nowUtc, RowVersion = row.RowVersion + 1 };
                _companies[index] = next;
                updated++;
                touched.Add(next);
            }

            var missing = 0;
            foreach (var id in plan.Missing)
            {
                var index = _companies.FindIndex(c => c.Id == id);
                if (index < 0 || _companies[index].State is CompanyState.Missing or CompanyState.Disabled)
                {
                    continue;
                }

                _companies[index] = _companies[index] with
                {
                    State = CompanyState.Missing,
                    UpdatedUtc = nowUtc,
                    RowVersion = _companies[index].RowVersion + 1,
                };
                missing++;
                touched.Add(_companies[index]);
            }

            _lastScan = nowUtc;
            _audit.Add((AuditActions.CompaniesScanned, null, actor));
            var items = touched.GroupBy(c => c.Id).Select(g => _companies.First(c => c.Id == g.Key)).OrderBy(c => c.Id).ToList();
            return Task.FromResult(new ScanApplied(added.Count, updated, missing, plan.Ambiguous, added, items));
        }
    }

    public Task<CompanyChange> UpdateAsync(
        int id, int expectedRowVersion, CompanyPatch patch, string actor, DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var index = _companies.FindIndex(c => c.Id == id);
            if (index < 0)
            {
                return Task.FromResult(new CompanyChange(CompanyChangeKind.NotFound, null));
            }

            var row = _companies[index];
            if (row.RowVersion != expectedRowVersion)
            {
                return Task.FromResult(new CompanyChange(CompanyChangeKind.Stale, row));
            }

            var next = row;
            if (patch.FilePath is { } path)
            {
                var candidate = row.CandidateFiles.FirstOrDefault(c => SamePath(c, path));
                if (candidate is null && !SamePath(row.FilePath, path))
                {
                    return Task.FromResult(new CompanyChange(CompanyChangeKind.Invalid, row, "filePath is not one of the company's candidate files"));
                }

                if (!SamePath(row.FilePath, path))
                {
                    next = next with { FilePath = candidate ?? path, Access = CompanyAccess.Unchecked, AccessCheckedUtc = null, AccessMessage = null };
                }

                if (next.State == CompanyState.Ambiguous)
                {
                    next = next with { State = CompanyState.Active };
                }
            }

            if (patch.Disabled is { } disabled)
            {
                if (disabled && row.IsDefault)
                {
                    return Task.FromResult(new CompanyChange(CompanyChangeKind.Invalid, row, "the default company cannot be disabled"));
                }

                if (disabled && next.State != CompanyState.Disabled)
                {
                    next = next with { State = CompanyState.Disabled };
                }
                else if (!disabled && next.State == CompanyState.Disabled)
                {
                    next = next with { State = CompanyRules.ReEnabledState(next.FilePath, next.CandidateFiles.Count, patch.FileExists) };
                }
            }

            if (patch.MatchNames is { } names)
            {
                var cleaned = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!cleaned.SequenceEqual(row.MatchNames, StringComparer.Ordinal))
                {
                    next = next with { MatchNames = cleaned };
                }
            }

            if (next == row)
            {
                return Task.FromResult(new CompanyChange(CompanyChangeKind.Changed, row));
            }

            next = next with { UpdatedUtc = nowUtc, RowVersion = row.RowVersion + 1 };
            _companies[index] = next;
            _audit.Add((AuditActions.CompanyUpdated, id, actor));
            return Task.FromResult(new CompanyChange(CompanyChangeKind.Changed, next));
        }
    }

    public Task<CompanyChange> RecordAccessAsync(int id, CompanyAccessCheck check, string actor, CancellationToken ct)
    {
        lock (_gate)
        {
            var index = _companies.FindIndex(c => c.Id == id);
            if (index < 0)
            {
                return Task.FromResult(new CompanyChange(CompanyChangeKind.NotFound, null));
            }

            var row = _companies[index];
            if (!SamePath(row.FilePath, check.CheckedFilePath))
            {
                return Task.FromResult(new CompanyChange(CompanyChangeKind.Stale, row));
            }

            var next = row with
            {
                Access = check.Access,
                AccessMessage = check.Message,
                AccessCheckedUtc = check.CheckedUtc,
                QbCompanyName = check.QbCompanyName ?? row.QbCompanyName,
                QbLegalName = check.QbLegalName ?? row.QbLegalName,
                UpdatedUtc = check.CheckedUtc,
                RowVersion = row.RowVersion + 1,
            };
            _companies[index] = next;
            _audit.Add((AuditActions.CompanyChecked, id, actor));
            return Task.FromResult(new CompanyChange(CompanyChangeKind.Changed, next));
        }
    }

    public Task<DateTime?> LastScanUtcAsync(CancellationToken ct)
    {
        lock (_gate) { return Task.FromResult(_lastScan); }
    }

    private int IndexFor(FolderChange change)
    {
        var byId = change.CompanyId > 0 ? _companies.FindIndex(c => c.Id == change.CompanyId) : -1;
        return byId >= 0
            ? byId
            : _companies.FindIndex(c => string.Equals(c.FolderName, change.Folder.FolderName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SamePath(string? a, string? b) =>
        a is null || b is null ? a is null && b is null : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
