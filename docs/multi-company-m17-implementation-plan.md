# Multiple QuickBooks company files (M17, backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One QbAutopost installation serves many QuickBooks Desktop company files: it finds them by scanning `Companies:RootFolder`, matches each uploaded PDF to a company from the name printed in it, and validates, syncs lists for and posts every statement against its own company's file, rules, ledger and lists.

**Architecture:** A `companies` table (one EF migration) plus nullable `company_id` columns; a pure matcher and a file-system scanner in Core; a `CompanyContext` (file, rules, ledger) resolved per statement and passed to the posting engine, list sync and validation; the one `ResilientQbGateway` lock carries a per-call `QbTarget`. Version 1 routes keep using the default company; the default company keeps the original configured files.

**Tech Stack:** .NET 8, ASP.NET Core minimal APIs, EF Core 8 + Npgsql (PostgreSQL 16), xUnit, Testcontainers.PostgreSql (Data tests), `WebApplicationFactory` (`ApiFactory`), `FakeQbGateway`, `FakeOpenRouterHandler`.

**Spec:** `docs/multi-company-m17.md` (contract) and `docs/multi-company-m17-plan.md` (approved task list T-1700…T-1715). Dashboard side: `D:\qbautopost\qb_post_react\docs\api-requirements-for-dashboard.md` R-18…R-20.

## Spec conflicts found

Each item is a place where the approved spec or plan cannot be implemented as written against the real code, or leaves
a choice the code forces. The proposed resolution is what this plan implements. T-1700 records each one as a Questions
row (Q-144 onwards) so the owner can overrule it.

1. **Backfill of `qb_list_syncs` vs. grants.** Spec §4.4 has `CompanyBootstrap` (running as `qbautopost_app`) set
   `company_id` on existing `qb_list_syncs` rows, but `deploy/grants.sql` gives the app role only `SELECT, INSERT,
   DELETE` on that table ("rows are never updated"). *Resolution:* T-1703 adds a column-level grant
   `GRANT UPDATE (company_id) ON qb_list_syncs TO qbautopost_app;` and amends the comment; nothing else becomes updatable.
2. **The default company would be locked out.** Spec §4.7 lets only `active` + `approved` companies open a session, and
   access starts `unchecked`; an upgraded single-company install would then answer 409 `company-not-ready` on every v2
   validate, post and list sync until an admin runs Check access, contradicting §4.6 ("a single-company install behaves
   exactly as today") and A-13. *Resolution:* `CompanyBootstrap` creates the default company `active` with access
   `approved` and `access_message` "the configured Company:FilePath, used before M17"; a later Check access may change it.
3. **Check access cannot release the lock early.** Spec §4.7 says the check runs under `QuickBooks:BusyTimeoutSeconds`
   "so a pending approval dialog ends as `needs-approval` instead of holding the lock forever". `ResilientQbGateway`
   deliberately keeps its one lock until an abandoned COM call really returns (FR-11: never two sessions), and a COM
   call cannot be cancelled. *Resolution:* the check answers `needs-approval` at the busy timeout, but the lock stays
   held until the dialog is answered or closed on the server; the lock rule is not changed. The T-1707 checklist
   records what `BeginSession` does for an unapproved file, and the T-1714 runbook tells the admin to answer the dialog.
4. **Company audit events have no home, and the audit view has no company.** `saved_request_events` needs a
   `saved_request_id`; `user_audit` rows are hidden from callers without `users:manage` (the store drops ids `u%`); the
   `audit_events` view has no company column, yet T-1713 must filter by `company` and every event must carry
   `companyId`; and the plan allows exactly one migration (T-1703). *Resolution:* `companies-scanned`,
   `company-updated` and `company-checked` are written to `user_audit` (`user_id` null, `kind` = the action name,
   `detail.companyId`) in the same transaction as the change. The T-1703 migration recreates `audit_events` with a
   `company_id` column (from `saved_requests.company_id` for `e`/`v` rows, from `detail->>'companyId'` for company rows),
   gives the three company kinds their own branch with id prefix `c` (so the `users:manage` filter does not hide them)
   and their own action names. T-1713 only reads it.
5. **Plan wording vs. spec on copies.** Plan T-1704 says "`CompanyBootstrap` (default company, rules/ledger copy, …)";
   spec §4.4 says the default company keeps `Company:RulesFile`, `Paths:Ledger` and `Company:FilePath`, no copies.
   *Resolution:* the spec wins. Bootstrap copies nothing. The copy (`RulesTemplate` → `<DataFolder>/<id>/rules.json`,
   empty `ledger.json`) is `CompanyDataFolder.Prepare` (T-1704), called for scan-created companies (T-1705).
6. **`L L C` in the drop list is ambiguous.** Dropping the single tokens `L` and `C` everywhere would also remove real
   initials (`C & L MARKET` → `MARKET`) and create false matches. *Resolution:* drop the consecutive token sequence
   `L`,`L`,`C` (what `L.L.C.` becomes); every other listed token is dropped wherever it stands.
7. **Saving a statement with no company still validates.** Every save, edit and revalidate builds a validation snapshot
   through `DirectPostRunner.Plan`, which needs a company's rules and lists; spec §4.6 gives a needs-company statement
   none. *Resolution:* with no company the snapshot is `unavailable` with the fixed error `company required`
   (`SavedRequestValidator.CompanyRequired`) and the plan does not run. `// SPEC-GAP T-1712`.
8. **v1 saved-request routes are not named.** Spec non-goals keep `/jobs` and v1 `/quickbooks/*` on the default
   company and A-10 pins v1 contracts; spec §4.6 says validate and post answer 409 `company-required` "regardless".
   *Resolution:* `/api/v1/saved-requests/{id}/validate|post` use the statement's own company and answer 409
   `company-required` / `company-not-ready` like v2; v1 views gain no field (the v1 OpenAPI and pinned bodies are
   unchanged). With multi-company off every statement has the default company, so v1 behaviour is unchanged there.
9. **`RulesEditor` and `CompanyFileValidator` have no v2 caller.** Spec §4.7 lists "`RulesEditor` (v2 use)" and
   `CompanyFileValidator` among the things that take a context, but both are used only by v1 routes (`/rules/*`,
   `/quickbooks/company-file/validate`). *Resolution:* both stay on the default company, unchanged; no v2 rules or
   company-file route is added (out of scope).
10. **Same condition, two status codes.** The new Check access answers 503 `quickbooks-not-open` (spec §5.1) while M14
    Sync now answers 409 for the same condition. *Resolution:* follow the spec for the new route; lists keep 409.
11. **`rowVersion` is missing from spec §5.1's `Company`.** `PATCH` needs `If-Match: "<rowVersion>"`, and R-18 lists
    `rowVersion`. *Resolution:* `Company` carries `rowVersion` and the single-company answers send it as `ETag`.
12. **Where `companyId` goes on an upload save.** "`POST /saved-requests` (upload) accepts optional `companyId`" does not
    say where; the n8n body must not change. *Resolution:* read `upload.companyId` (inside the existing `upload`
    object, beside `extractionId`); a top-level `companyId` is ignored. The dashboard plan must send it there.
13. **Order of company sources on save.** Spec §4.6 lists "chosen, else matched, else null" and separately "one active
    company → that company, source `default`". *Resolution:* `upload.companyId` (chosen) → the automatic company when
    multi-company is off or one company is active (default) → the match (matched) → null.
14. **Plan order would break the suite.** T-1708 (company context in posting) runs before T-1712 (statements get a
    company on save), so a `company-required` hold in T-1708 would refuse every post in the existing tests.
    *Resolution:* until T-1712, a statement with no company resolves to the default company (`// T-1712` marker); T-1712
    replaces that with the hold. Likewise T-1708 plans with the default lists until T-1709 makes them per company.

## Global Constraints

- `/api/v1/**` and the v1 OpenAPI document stay byte-identical (`V1ContractPinTests`, `OpenApiDriftTests`); v1 `/jobs`, `JobPipeline` and v1 `/quickbooks/*` use the default company.
- No new NuGet package, hosted service or timer: the single `JobWorker` runs the scheduled sweep.
- One new table (`companies`) and new nullable columns, via one EF migration `MultiCompany`; `deploy/migrations.sql` regenerated with `dotnet ef migrations script --idempotent -p src/QbAutopost.Data -s src/QbAutopost.Api -o deploy/migrations.sql`.
- COM only in `src/QbAutopost.QuickBooks`; no test contacts real QuickBooks or OpenRouter (`FakeQbGateway`, `FakeHermesClient`, `FakeOpenRouterHandler`); `dotnet test` passes on Linux.
- One `ResilientQbGateway` and one lock for all companies.
- The statement file name is never used for matching and never sent to the model for the company fields.
- Matching is exact equality after normalising; no fuzzy distance. The model only reads the printed name; code decides.
- Only `active` + `approved` companies open a QuickBooks session, except the admin's Check access.
- Never close, switch or drive the QuickBooks UI; never retry a refused (wrong-company) post.
- Never open a `.qbw` outside `Companies:RootFolder`, except the configured default `Company:FilePath`.
- Hold, don't guess: anything unresolved posts less and gets `// SPEC-GAP T-17xx` plus a Questions row.
- Problem types, exactly: `company-required`, `company-not-ready` (with `state`, `access`), `company-file-open-elsewhere` (with `openFile`, `neededFile`), `company-file-unavailable`, `company-root-unavailable`, `quickbooks-not-open`.
- Audit actions, exactly: `companies-scanned`, `company-updated`, `company-checked`, `company-assigned`.
- New prompt version `2026-11-03.api-2`; new top-level answer fields `accountHolderName`, `dbaName`.
- Settings `Companies:RootFolder`, `Companies:DataFolder` (default `data/companies`), `Companies:RulesTemplate` (default `data/rules.json`), `Companies:ScheduledSync` (`waiting-only` default, `all`, `off`).
- Tests: xUnit, `Should_<behaviour>_When_<condition>`, one behaviour per test, fixtures over inline blobs.
- Each task: tests first → implement → `dotnet build -warnaserror` (0 warnings) → `dotnet test` → tracker row (Status, Files, Verified by) + Session-log line → commit `T-17xx: <summary>`.
- Commits stage only the task's own files. These files carry someone else's uncommitted changes and are never staged unless a task says so: `deploy/start-all.ps1`, `samples/*`, `scripts/qb-server-check.ps1`, `src/QbAutopost.Api/appsettings.json`, `tests/QbAutopost.Api.Tests/Api/ReferenceUiTests.cs`, `response.json`.

## Review Focus

- A printed or stored name that normalises to nothing (`LLC`, `THE CO.`, `&`, blank) must never match anything, and two companies whose names normalise to nothing must not make every statement `ambiguous` (T-1701).
- Case-only and extension-case differences on Windows (`18_TAKOMA PARK…` vs `18_Takoma Park…`, `.QBW` vs `.qbw`) must be the same company: no second row, no `missing` (T-1702, T-1703).
- A company that changes between validate and post (disabled, file re-pointed, access no longer `approved`) must be re-checked when the post is claimed: 409 `company-not-ready`, no session, no attempt; and a company cannot be changed while a post is in progress (T-1708, T-1712).
- Malformed or unusable company ids (`company=abc`, `company=0`, repeated `company`, a `companyId` of a `missing` or `disabled` company, an unknown id) must answer 400 `invalid-request` rather than fall back to the default company; a well-formed unknown id in a list filter returns an empty page (T-1709, T-1712).
- A first boot interrupted between creating the default company and backfilling old rows must not leave old statements as "needs company" or create a second default: creation and backfill are one transaction (T-1703, T-1704).

---

## File structure

| File | Responsibility | Task |
|---|---|---|
| `docs/tracker.md`, `docs/adr/0013-multiple-company-files.md` | M17 rows, Questions, Decisions, ADR | T-1700 |
| `src/QbAutopost.Core/Companies/CompanyNameMatcher.cs`, `CompanyMatch.cs` | normalise + exact match | T-1701 |
| `src/QbAutopost.Core/Companies/{CompanyFolderScanner,ICompanyFolderSource,ScanResult,CompanyState}.cs` | one-level scan + reconcile with known companies | T-1702 |
| `src/QbAutopost.Core/Companies/{Company,CompanyContext,ICompanyStore,ICompanyResolver}.cs` | company model, context, store and resolver contracts | T-1703 |
| `src/QbAutopost.Data/{Entities/CompanyEntity,Configurations/CompanyConfiguration,EfCompanyStore}.cs`, migration | table, columns, view, store | T-1703 |
| `src/QbAutopost.Api/Configuration/AppSettings.cs` (+ `CompaniesSettings`, `CompaniesOptionsValidator`) | `Companies` settings | T-1704 |
| `src/QbAutopost.Api/Companies/{CompanyBootstrap,CompanyResolver,CompanyDataFolder}.cs` | default company, contexts, per-company files | T-1704 |
| `src/QbAutopost.Api/Companies/CompanyProblems.cs`, `Endpoints/V2/CompanyEndpoints.cs`, `Endpoints/ApiRoutes.cs`, `wwwroot/openapi-v2.json` | companies routes, scopes, problems | T-1705, T-1710 |
| `src/QbAutopost.Core/Abstractions/IQbGateway.cs`, `Core/Gateway/{ResilientQbGateway,TargetedQbGateway}.cs`, `QuickBooks/{QbGateway,QbSession,QbSessionErrors}.cs`, `Api/QuickBooks/{LoggingQbGateway,SimulatedQbGateway,UnconfiguredQbGateway,QbConnection}.cs`, `tests/**/FakeQbGateway.cs` | per-call target, wrong-company error | T-1706 |
| `docs/testing/T-1707.server-checklist.md` | server verification | T-1707 |
| `Jobs/{DirectPostRunner,SyncPostRunner}.cs`, `Pipeline/QbListSync.cs`, `SavedRequests/{SavedRequestPoster,SavedRequestValidator}.cs`, `Endpoints/SyncPostResponses.cs`, `Core/SavedRequests/{PostEnding,AttemptOutcomes}.cs`, `Core/Api/DirectPostResult.cs`, `Program.cs` | company context through posting | T-1708 |
| `Core/Abstractions/IQbListsCatalog.cs`, `QuickBooks/DbQbListsSource.cs`, `Data/EfQbListsStore.cs`, `Endpoints/V2/QuickBooksListsV2Endpoints.cs`, `Jobs/{JobWorker,ListSyncSchedule,CompanySweep}.cs` | lists per company, sweep | T-1709 |
| `Core/QbXml/QbCompanyQuery.cs`, `Api/Companies/CompanyAccessChecker.cs` | Check access | T-1710 |
| `Extraction/{extraction-prompt.json,StatementExtractionService}.cs`, `Core/Extraction/{ExtractionAnswer,StatementExtraction}.cs` | printed names + match | T-1711 |
| `SavedRequests/{SavedRequestIntake,SavedRequestViewsV2}.cs`, `Endpoints/SavedRequestEndpoints*.cs`, `Data/EfSavedRequestStore*.cs`, `Core/SavedRequests/{DuplicateDetector,SavedRequestRules,SavedRequestModels,SavedRequestV2Models}.cs` | company on statements, hold, set-company | T-1712 |
| `Endpoints/V2/{DashboardSummaryEndpoints,AuditEventEndpoints}.cs`, `Core/Dashboard/IDashboardSummaryStore.cs`, `Core/SavedRequests/Audit/{AuditQuery,AuditEvent}.cs` | `company` filter | T-1713 |
| `Health/Readiness.cs`, `Logging/StartupLog.cs`, `docs/runbook.md` | readiness, startup line, runbook | T-1714 |
| `docs/testing/T-1715.server-checklist.md` | end-to-end on the server | T-1715 |

Paths below without a project prefix are under `src/QbAutopost.Api/`.

---

### Task T-1700: Tracker M17, Questions, ADR-0013

**Files:**
- Modify: `docs/tracker.md` (Milestones table, new `## M17 — Multiple QuickBooks company files` section after M16, Questions, Decisions, Session log)
- Create: `docs/adr/0013-multiple-company-files.md` (from `docs/adr/template.md`)
- Test: `tests/QbAutopost.Core.Tests/Architecture/OperatorDocsTests.cs` (existing, unchanged)

**Interfaces:**
- Consumes: nothing.
- Produces: tracker rows T-1700…T-1715 (`todo`), Questions Q-133… referenced by `// SPEC-GAP T-17xx` markers in later tasks.

- [ ] **Step 1: Add the M17 milestone row and section.** Milestones row: `| M17 | Multiple QuickBooks company files (R-18…R-20) | 16 | 0 | in progress (branch feature/9-addition-of-company-file) — contract docs/multi-company-m17.md, plan docs/multi-company-m17-plan.md |`. Section table with the M1-style columns `| ID | Task | Spec | Files | Verified by | Status | Notes |`, one row per T-1700…T-1715 copied from the plan's task table, Status `todo` (T-1700 `done` at the end of this task).
- [ ] **Step 2: Add Questions rows.** Q-133…Q-143 = spec §6 a…k (question and call as written there, raised by `T-1700`). Q-144 onwards = each item of "Spec conflicts found" above, one row each, with the resolution as the answer.
- [ ] **Step 3: Supersede Q-44 and Q-50.** Append to each row's last column: `Superseded by ADR-0013 (M17): one installation serves many company files; for v2 routes the company comes from the statement.` Never delete the rows.
- [ ] **Step 4: Decisions row.** `| 2026-11-04 | M17 | One new table companies and nullable company_id columns via one migration (MultiCompany); approved by the M17 spec | ADR-0013 |` (match the Decisions table's columns).
- [ ] **Step 5: Write ADR-0013** "Multiple company files": context (Q-44, Q-50, QuickBooks one-file-at-a-time and one-time certificate approval per file), decision (spec §1 goals, the scan, exact matching, `CompanyContext`, one gateway lock with a per-call target, default company keeps the original files), consequences (N approvals, wrong-file refusal, v1 unchanged). Status Accepted, date 2026-11-04.
- [ ] **Step 6: Verify.** Run `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~OperatorDocsTests"` — PASS.
- [ ] **Step 7: Tracker row and Session-log line** (T-1700 `done`, Files, "OperatorDocsTests green; docs only").
- [ ] **Step 8: Commit**

```bash
git add docs/tracker.md docs/adr/0013-multiple-company-files.md
git commit -m "T-1700: M17 tracker section, questions Q-133 on, ADR-0013"
```

---

### Task T-1701: `CompanyNameMatcher`

**Files:**
- Create: `src/QbAutopost.Core/Companies/CompanyNameMatcher.cs`, `src/QbAutopost.Core/Companies/CompanyMatch.cs`
- Test: `tests/QbAutopost.Core.Tests/Companies/CompanyNameMatcherTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:

```csharp
namespace QbAutopost.Core.Companies;
public enum CompanyMatchKind { Matched, None, Ambiguous }
/// <param name="Matchable">False for missing and disabled companies (they never match); true for active and ambiguous.</param>
public sealed record CompanyNames(int CompanyId, bool Matchable, IReadOnlyList<string?> Names);
/// <param name="Candidates">Matched: [the id]; Ambiguous: every matching id, ascending; None: [].</param>
public sealed record CompanyMatch(CompanyMatchKind Match, string? PrintedName, string? DbaName, int? CompanyId, IReadOnlyList<int> Candidates)
{
    public static CompanyMatch NoneFor(string? printedName, string? dbaName);
}
public static class CompanyNameMatcher
{
    public static string Normalize(string? name);
    public static CompanyMatch Match(string? printedName, string? dbaName, IEnumerable<CompanyNames> companies);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("S & A CONVENIENCE MARKET LLC", "SACONVENIENCEMARKET")]
[InlineData("SA CONVENIENCE MARKET", "SACONVENIENCEMARKET")]
[InlineData("TAKOMA PARK GROCERY STORE INC", "TAKOMAPARKGROCERYSTORE")]
[InlineData("Takoma Park Grocery Store Inc", "TAKOMAPARKGROCERYSTORE")]
[InlineData("DBA ZEMEN MARKET", "ZEMENMARKET")]
[InlineData("The Acme Co., L.L.C.", "ACME")]
[InlineData("C & L Market", "CLMARKET")]           // single L and C are kept (conflict 6)
[InlineData("Acme Corporation Ltd", "ACME")]
public void Should_NormaliseToJoinedTokens_When_NameHasPunctuationAndSuffixes(string name, string expected) =>
    Assert.Equal(expected, CompanyNameMatcher.Normalize(name));

[Fact] public void Should_MatchTakoma_When_HeaderPrintsTakomaParkGroceryStoreInc()
// companies: 1 "Takoma Park Grocery Store Inc", 2 "S & A Convenience Market LLC"
// Match("TAKOMA PARK GROCERY STORE INC", "DBA ZEMEN MARKET", …) → Matched, CompanyId 1, Candidates [1]

[Fact] public void Should_MatchSAndA_When_HeaderPrintsSaConvenienceMarket()
// Match("SA CONVENIENCE MARKET", null, …) → Matched, CompanyId 2

[Fact] public void Should_MatchOnDbaAlias_When_AliasIsAMatchName()
// company 1 names include "Zemen Market"; Match(null, "DBA ZEMEN MARKET") → Matched 1

[Fact] public void Should_ReturnNone_When_NameIsUnknown()        // "UNKNOWN STORE LLC" → None, CompanyId null, Candidates []
[Fact] public void Should_ReturnAmbiguous_When_TwoCompaniesShareANormalisedName()
// 1 "S & A Convenience Market LLC", 3 "SA Convenience Market" → Ambiguous, CompanyId null, Candidates [1, 3]
[Fact] public void Should_IgnoreCompany_When_NotMatchable()      // only match is Matchable=false → None
[Fact] public void Should_ReturnNone_When_PrintedNameNormalisesToNothing()
// Match("LLC", "THE CO.", companies incl. one whose names are [null, "", "INC"]) → None (Review Focus 1)
[Fact] public void Should_KeepPrintedNamesAsPrinted_When_Matching()  // PrintedName/DbaName echo the inputs unchanged
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~CompanyNameMatcherTests"` — FAIL (types not defined).
- [ ] **Step 3: Implement `Normalize` in `CompanyNameMatcher.cs`.** The algorithm (the one body this plan fixes):

```csharp
private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    { "LLC", "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "LTD", "LIMITED", "THE", "DBA" };

public static string Normalize(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return "";
    var chars = name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray(); // '&' and all else → space
    var tokens = new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    for (var i = 0; i + 2 < tokens.Count; i++)                       // the sequence L L C (from "L.L.C."), conflict 6
        if (tokens[i] == "L" && tokens[i + 1] == "L" && tokens[i + 2] == "C") { tokens.RemoveRange(i, 3); i--; }
    return string.Concat(tokens.Where(t => !Dropped.Contains(t)));
}
```

- [ ] **Step 4: Implement `Match`.** Printed keys = non-empty `Normalize` of `printedName` and `dbaName`. A `Matchable` company matches when any non-empty normalised name equals any printed key (ordinal). One matching company → `Matched`; more → `Ambiguous` (ids ascending); none or no printed key → `CompanyMatch.NoneFor`.
- [ ] **Step 5: Run the tests** (same filter) — PASS. Then `dotnet build -warnaserror` (0 warnings) and `dotnet test` — all green.
- [ ] **Step 6: Tracker row T-1701 and Session-log line.**
- [ ] **Step 7: Commit**

```bash
git add src/QbAutopost.Core/Companies/CompanyNameMatcher.cs src/QbAutopost.Core/Companies/CompanyMatch.cs tests/QbAutopost.Core.Tests/Companies/CompanyNameMatcherTests.cs docs/tracker.md
git commit -m "T-1701: CompanyNameMatcher, exact match after normalising"
```

---

### Task T-1702: `CompanyFolderScanner`

**Files:**
- Create: `src/QbAutopost.Core/Companies/CompanyFolderScanner.cs`, `ICompanyFolderSource.cs` (interface + `FileSystemCompanyFolderSource`), `ScanResult.cs`, `CompanyState.cs`
- Test: `tests/QbAutopost.Core.Tests/Companies/CompanyFolderScannerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:

```csharp
public enum CompanyState { Active, Ambiguous, Missing, Disabled }
public interface ICompanyFolderSource
{
    /// <summary>Immediate subfolder names. Throws IOException/UnauthorizedAccessException/DirectoryNotFoundException when the root cannot be read.</summary>
    IReadOnlyList<string> Subfolders(string rootFolder);
    /// <summary>Full paths of the files directly in <paramref name="folderPath"/> whose extension is .qbw (OrdinalIgnoreCase); not recursive.</summary>
    IReadOnlyList<string> CompanyFiles(string folderPath);
}
public sealed class FileSystemCompanyFolderSource : ICompanyFolderSource;
public sealed record ScannedFolder(string FolderName, string? Code, string DisplayName, IReadOnlyList<string> Files);
public sealed record ScanResult(IReadOnlyList<ScannedFolder> Folders, IReadOnlyList<string> NoCompanyFile);
public sealed class CompanyRootUnavailableException(string rootFolder, Exception? inner) : Exception(...) { public string RootFolder { get; } }
public sealed record KnownCompanyFolder(int Id, string FolderName, string? FilePath, CompanyState State, bool IsDefault);
public sealed record FolderChange(int CompanyId, ScannedFolder Folder, string? FilePath, CompanyState State); // CompanyId 0 = new
public sealed record ScanPlan(IReadOnlyList<FolderChange> Add, IReadOnlyList<FolderChange> Update, IReadOnlyList<int> Missing, IReadOnlyList<string> NoCompanyFile)
{
    public int Ambiguous => Add.Concat(Update).Count(c => c.State == CompanyState.Ambiguous);
}
public sealed class CompanyFolderScanner(ICompanyFolderSource source)
{
    public ScanResult Scan(string rootFolder);                        // wraps source exceptions in CompanyRootUnavailableException
    public static (string? Code, string DisplayName) ParseFolderName(string folderName);
    public static ScanPlan Reconcile(ScanResult scan, IReadOnlyList<KnownCompanyFolder> known);
}
```

- [ ] **Step 1: Write the failing tests** (temp folders via `Directory.CreateTempSubdirectory("qbautopost-scan-")`, deleted in `Dispose`; the real `FileSystemCompanyFolderSource`):

```csharp
[Fact] public void Should_FindTwoCompaniesWithCodes_When_EachSubfolderHoldsOneQbw()   // A-1 at Core level
// root/18_Takoma Park Grocery Store Inc/Takoma Park Grocery Store Inc.qbw, root/440_S & A Convenience Market LLC/S & A Convenience Market LLC.qbw
// → two folders; ("18","Takoma Park Grocery Store Inc"), ("440","S & A Convenience Market LLC"); Reconcile(scan, []) → Add.Count 2, both Active
[Fact] public void Should_AddNothing_When_ScannedTwice()                // Reconcile with both known (same paths) → Add 0, Update 0, Missing 0
[Theory]
[InlineData("18_Takoma Park Grocery Store Inc", "18", "Takoma Park Grocery Store Inc")]
[InlineData("Main Street Deli", null, "Main Street Deli")]
[InlineData("440_S & A Convenience Market LLC", "440", "S & A Convenience Market LLC")]
[InlineData("x18_Shop", null, "x18_Shop")]
public void Should_SplitCodeAndName_When_FolderStartsWithDigitsAndUnderscore(string folder, string? code, string name)
[Fact] public void Should_IgnoreSideFiles_When_FolderHoldsTlgNdQbbQbmDsn()  // only Company.qbw counted
[Fact] public void Should_CountUpperCaseExtension_When_FileIsDotQBW()     // "Company.QBW" counted
[Fact] public void Should_IgnoreNestedFolders_When_QbwIsTwoLevelsDown()    // root/A/old/x.qbw → A in NoCompanyFile
[Fact] public void Should_ReportNoCompanyFile_When_SubfolderHasNoQbw()
[Fact] public void Should_MarkAmbiguousWithBothCandidates_When_SubfolderHoldsTwoQbw()   // A-3 at Core level
[Fact] public void Should_KeepChosenFile_When_AmbiguousFolderIsRescannedAfterChoice()   // known FilePath ∈ Files → Update with Active, or no change
[Fact] public void Should_UpdateFilePath_When_QbwWasRenamed()
[Fact] public void Should_MarkAmbiguous_When_DefaultCompanyFolderHoldsADifferentQbw()   // IsDefault, single file ≠ FilePath → Ambiguous
[Fact] public void Should_MarkMissing_When_SubfolderDisappeared()                         // A-2 at Core level
[Fact] public void Should_ReturnToActive_When_MissingSubfolderReappears()
[Fact] public void Should_MatchKnownFolder_When_OnlyCaseDiffers()          // Review Focus 2: "18_TAKOMA PARK GROCERY STORE INC" known as "18_Takoma…" → Update, not Add + Missing
[Fact] public void Should_NotChangeState_When_CompanyIsDisabled()
[Fact] public void Should_ThrowRootUnavailable_When_RootDoesNotExist()
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~CompanyFolderScannerTests"` — FAIL.
- [ ] **Step 3: Implement `FileSystemCompanyFolderSource`** with `Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)` and `Directory.EnumerateFiles(folder, "*", TopDirectoryOnly)` filtered on `Path.GetExtension(f).Equals(".qbw", OrdinalIgnoreCase)`; results sorted ordinal-ignore-case so scans are deterministic.
- [ ] **Step 4: Implement `Scan` and `ParseFolderName`.** `ParseFolderName`: regex `^(\d+)_(.+)$` → code = group 1, displayName = group 2 trimmed; otherwise `(null, folderName.Trim())`.
- [ ] **Step 5: Implement `Reconcile`.** Known folders are matched by `FolderName` with `StringComparer.OrdinalIgnoreCase`; paths compared with `OrdinalIgnoreCase` after `Path.GetFullPath`. Rules: unknown folder → `Add` (one file → Active with that file; several → Ambiguous, `FilePath` null). Known, not disabled: one file equal to `FilePath` and not missing → no change; one different file → default company: Ambiguous with `FilePath` kept; others: Update to the new file, Active; several files: `FilePath` among them → Active (candidates refreshed), else Ambiguous; was Missing → returns Active/Ambiguous. Known disabled: Update file/candidates only, state stays Disabled. Known folder absent from the scan (or now with no `.qbw`, `// SPEC-GAP T-1702`) and not already Missing/Disabled → `Missing`. The default company's row with `FolderName` null never takes part.
- [ ] **Step 6: Run the tests** — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 7: Tracker row T-1702 (+ Questions row for the no-`.qbw`-means-missing gap) and Session-log line.**
- [ ] **Step 8: Commit**

```bash
git add src/QbAutopost.Core/Companies/CompanyFolderScanner.cs src/QbAutopost.Core/Companies/ICompanyFolderSource.cs src/QbAutopost.Core/Companies/ScanResult.cs src/QbAutopost.Core/Companies/CompanyState.cs tests/QbAutopost.Core.Tests/Companies/CompanyFolderScannerTests.cs docs/tracker.md
git commit -m "T-1702: CompanyFolderScanner, one-level scan and reconcile"
```

---

### Task T-1703: Data — `companies`, `company_id` columns, `MultiCompany` migration, `EfCompanyStore`

**Files:**
- Create: `src/QbAutopost.Core/Companies/{Company,CompanyContext,ICompanyStore,ICompanyResolver}.cs`
- Create: `src/QbAutopost.Data/Entities/CompanyEntity.cs`, `src/QbAutopost.Data/Configurations/CompanyConfiguration.cs`, `src/QbAutopost.Data/EfCompanyStore.cs`, `src/QbAutopost.Data/Migrations/<ts>_MultiCompany.cs` (+ Designer, snapshot)
- Modify: `src/QbAutopost.Data/Entities/{SavedRequestEntity,StatementExtractionEntity,QbListSyncEntity,AuditEventRow}.cs` + their configurations, `src/QbAutopost.Data/QbAutopostDbContext.cs`, `src/QbAutopost.Data/DataServiceCollectionExtensions.cs`, `src/QbAutopost.Data/EfSavedRequestStore.cs` (read mapping), `src/QbAutopost.Core/SavedRequests/SavedRequestModels.cs`, `src/QbAutopost.Core/Extraction/StatementExtraction.cs`, `src/QbAutopost.Core/SavedRequests/Audit/AuditActions.cs`, `deploy/grants.sql`, `deploy/migrations.sql`
- Create: `tests/QbAutopost.Api.Tests/TestSupport/FakeCompanyStore.cs`
- Test: `tests/QbAutopost.Data.Tests/{CompanyStoreContract,FakeCompanyStoreTests,EfCompanyStoreTests,MultiCompanyMigrationTests}.cs`

**Interfaces:**
- Consumes: `CompanyState`, `ScanPlan`, `FolderChange` (T-1702); `CompanyNames`, `CompanyMatch` (T-1701).
- Produces:

```csharp
public enum CompanyAccess { Unchecked, Approved, NeedsApproval, FileNotFound, Error }
public enum CompanySource { Matched, Chosen, Migrated, Default }
public sealed record Company(
    int Id, string? FolderName, string? Code, string DisplayName, string? FilePath, IReadOnlyList<string> CandidateFiles,
    CompanyState State, CompanyAccess Access, DateTime? AccessCheckedUtc, string? AccessMessage,
    string? QbCompanyName, string? QbLegalName, IReadOnlyList<string> MatchNames, bool IsDefault,
    DateTime CreatedUtc, DateTime UpdatedUtc, int RowVersion)
{
    public bool CanOpenSession => State == CompanyState.Active && Access == CompanyAccess.Approved;
    public CompanyNames ForMatching() => new(Id, State is CompanyState.Active or CompanyState.Ambiguous,
        [DisplayName, QbCompanyName, QbLegalName, .. MatchNames]);
}
public static class CompanyText   // kebab-case wire names: "needs-approval", "file-not-found", "waiting-only" …
{
    public static string Of(CompanyState v); public static string Of(CompanyAccess v);
    public static string Of(CompanySource v); public static string Of(CompanyMatchKind v);
}
public sealed record CompanyContext(int Id, string DisplayName, string FilePath, string RulesFile, string LedgerFile)
{
    public bool IsDefault { get; init; }
    public CompanyState State { get; init; } = CompanyState.Active;
    public CompanyAccess Access { get; init; } = CompanyAccess.Approved;
    public bool CanOpenSession => State == CompanyState.Active && Access == CompanyAccess.Approved;
}
public sealed record NewDefaultCompany(string DisplayName, string FilePath, string? FolderName, string? Code);
public sealed record DefaultCompanyResult(Company Company, bool Created, int Backfilled);
public sealed record CompanyPatch(IReadOnlyList<string>? MatchNames, string? FilePath, bool? Disabled);
public enum CompanyChangeKind { Changed, NotFound, Stale, Invalid }
public sealed record CompanyChange(CompanyChangeKind Kind, Company? Company, string? Error = null);
public sealed record CompanyAccessCheck(CompanyAccess Access, string? Message, string? QbCompanyName, string? QbLegalName, DateTime CheckedUtc);
public sealed record ScanApplied(int Added, int Updated, int Missing, int Ambiguous, IReadOnlyList<int> AddedIds, IReadOnlyList<Company> Items);
public interface ICompanyStore
{
    Task<IReadOnlyList<Company>> ListAsync(CancellationToken ct);                 // id order
    Task<Company?> GetAsync(int id, CancellationToken ct);
    /// <summary>Creates the default company if the table is empty and, in the same transaction, sets company_id = it
    /// (and saved_requests.company_source = 'migrated') on every saved_requests, statement_extractions and
    /// qb_list_syncs row whose company_id is null. Otherwise returns the existing default, Created false, Backfilled 0.</summary>
    Task<DefaultCompanyResult> EnsureDefaultAsync(NewDefaultCompany company, DateTime nowUtc, CancellationToken ct);
    Task<ScanApplied> ApplyScanAsync(ScanPlan plan, string actor, DateTime nowUtc, CancellationToken ct);       // + audit companies-scanned
    Task<CompanyChange> UpdateAsync(int id, int expectedRowVersion, CompanyPatch patch, string actor, DateTime nowUtc, CancellationToken ct); // + company-updated
    Task<CompanyChange> RecordAccessAsync(int id, CompanyAccessCheck check, string actor, CancellationToken ct);                         // + company-checked
    Task<DateTime?> LastScanUtcAsync(CancellationToken ct);
}
public interface ICompanyResolver   // implemented in T-1704
{
    CompanyContext Default { get; }
    bool MultiCompany { get; }
    Task<CompanyContext?> GetAsync(int companyId, CancellationToken ct);
    Task<CompanyContext?> AutoAssignAsync(CancellationToken ct);
}
// SavedRequest and SavedRequestSummary gain init props: int? CompanyId; CompanySource? CompanySource; CompanyMatch? CompanyMatch (SavedRequest only)
// StatementExtraction gains init prop: CompanyMatch? Company
// AuditActions gains: CompaniesScanned = "companies-scanned", CompanyUpdated = "company-updated", CompanyChecked = "company-checked", CompanyAssigned = "company-assigned" (and in All)
```

- [ ] **Step 1: Write the store contract** `CompanyStoreContract` (abstract, `[ContractFact]`, `protected abstract ICompanyStore CreateStore();` plus `protected abstract Task<long> SeedUnassignedRequestAsync()` for the backfill case), with `FakeCompanyStoreTests` and `EfCompanyStoreTests` (`[RequiresPostgres]`, pattern of `EfExtractionStoreTests`):

```csharp
[ContractFact] public async Task Should_CreateDefaultOnceAndBackfill_When_TableIsEmpty()
// EnsureDefaultAsync twice → first Created true, IsDefault, State Active, Access Approved (conflict 2); second Created false, same Id; ListAsync count 1
[ContractFact] public async Task Should_AddActiveAndAmbiguousCompanies_When_ScanPlanApplied()
// plan from Reconcile(two folders + one two-file folder) → Added 3, Ambiguous 1, items carry CandidateFiles
[ContractFact] public async Task Should_UpsertByFolderName_When_SameFolderAppliedAgain()   // no second row, file path updated
[ContractFact] public async Task Should_MarkMissing_When_PlanSaysMissing()
[ContractFact] public async Task Should_ReturnStale_When_RowVersionDoesNotMatch()         // UpdateAsync(id, rv-1, …) → Stale, nothing written
[ContractFact] public async Task Should_RefuseFilePath_When_NotACandidate()               // → Invalid
[ContractFact] public async Task Should_MakeActive_When_FilePathChosenFromCandidates()    // A-3 store half
[ContractFact] public async Task Should_IncrementRowVersion_When_Changed()
[ContractFact] public async Task Should_RecordAccessAndNames_When_AccessChecked()
[ContractFact] public async Task Should_ReturnLastScanTime_When_Scanned()
```

`FakeCompanyStore` (Api.Tests TestSupport): in-memory, lock-guarded, `IReadOnlyList<(string Kind, int? CompanyId, string Actor)> AuditEvents` for API tests, and `Company Add(Company c)` seeding helper.

- [ ] **Step 2: Write `MultiCompanyMigrationTests`** (pattern of `DashboardM12MigrationTests`):

```csharp
[PostgresFact] public async Task Should_CreateCompaniesTableAndNullableCompanyColumns_When_Migrated()
// information_schema: companies(id, folder_name, code, display_name, file_path, candidate_files jsonb, state, access,
// access_checked_utc, access_message, qb_company_name, qb_legal_name, match_names jsonb, is_default, created_utc, updated_utc, row_version);
// saved_requests.company_id/company_source/company_match, statement_extractions.company_id/company_match, qb_list_syncs.company_id — all is_nullable YES
[PostgresFact] public async Task Should_ExposeCompanyIdInAuditView_When_Migrated()          // audit_events has company_id
[Theory] companies: SELECT/INSERT/UPDATE true, DELETE false (app role)
[PostgresFact] public async Task Should_AllowUpdateOfCompanyIdOnly_When_AppRoleUpdatesQbListSyncs()
// has_column_privilege(app,'qb_list_syncs','company_id','UPDATE') true; ('outcome','UPDATE') false (conflict 1)
[Fact] public void Should_NameCompaniesTableInGrants_When_ReadingGrants()
```

- [ ] **Step 3: Run to verify they fail.** `dotnet test tests/QbAutopost.Data.Tests --filter "FullyQualifiedName~CompanyStore|FullyQualifiedName~MultiCompanyMigration"` — FAIL (needs Docker for the Postgres half; skipped tests are not a pass).
- [ ] **Step 4: Core types** as in Interfaces; add `CompanyId`, `CompanySource`, `CompanyMatch` init props to `SavedRequest`/`SavedRequestSummary` and `Company` to `StatementExtraction` (defaults null, so v1 call sites and views are unchanged); add the four `AuditActions`.
- [ ] **Step 5: Entities and configuration.** `CompanyEntity` (snake_case via `SnakeCaseNames`): `Id` identity-always, `State`/`Access` text with `KebabCaseEnumConverter`, `CandidateFiles`/`MatchNames` jsonb (`List<string>`), `RowVersion` `IsConcurrencyToken()`; unique partial index `ix_companies_folder_name` on `folder_name` `WHERE folder_name IS NOT NULL`; unique partial index `ix_companies_is_default` on `is_default` `WHERE is_default`. New nullable FKs (`ON DELETE RESTRICT`) `saved_requests.company_id`, `statement_extractions.company_id`, `qb_list_syncs.company_id`; `saved_requests.company_source` text (kebab enum), `saved_requests.company_match` and `statement_extractions.company_match` jsonb. Indexes `ix_saved_requests_company_id_status` and `ix_qb_list_syncs_company_id_outcome_id` (company_id, outcome, id desc). `AuditEventRow.CompanyId` (`int?`).
- [ ] **Step 6: Generate the migration.** `dotnet ef migrations add MultiCompany -p src/QbAutopost.Data -s src/QbAutopost.Api`. Then hand-add to `Up`: `DROP VIEW audit_events;` and the new `CREATE VIEW audit_events AS` — the T-1106 view SQL with a `company_id` column added to each branch (`e`/`v`: `(SELECT company_id FROM saved_requests r WHERE r.id = e.saved_request_id)`; user branch: `NULL::integer`, `WHERE u.kind NOT IN ('companies-scanned','company-updated','company-checked')`), plus a fourth branch over `user_audit` for those three kinds: id `'c' || u.id`, action = `u.kind`, `request_id` null, detail = `u.detail`, `company_id` = `(u.detail ->> 'companyId')::integer`. `Down` restores the T-1106 SQL verbatim.
- [ ] **Step 7: Grants.** In `deploy/grants.sql`: add `companies` to the `GRANT SELECT, INSERT, UPDATE ON` list; add `GRANT UPDATE (company_id) ON qb_list_syncs TO qbautopost_app;` with a comment "M17: the one-time backfill of company_id (spec §4.4); no other column is ever updated"; amend the header comment.
- [ ] **Step 8: `EfCompanyStore`** (`IDbContextFactory<QbAutopostDbContext>`), registered in `AddQbAutopostData` as `ICompanyStore` singleton. `EnsureDefaultAsync` runs in one transaction with `SELECT … FOR UPDATE`-free logic: insert only if `NOT EXISTS (SELECT 1 FROM companies)`, then three `ExecuteUpdateAsync` backfills (Review Focus 5); a unique-index violation from a racing second process is caught and the existing default returned. `ApplyScanAsync`, `UpdateAsync`, `RecordAccessAsync` each insert their `user_audit` row (`Kind` = action, `Actor`, `Detail` JSON with `companyId` or the counts) in the same `SaveChanges`. `EfSavedRequestStore` read mapping fills the new `SavedRequest`/`SavedRequestSummary`/`StatementExtraction` props.
- [ ] **Step 9: Regenerate the script.** `dotnet ef migrations script --idempotent -p src/QbAutopost.Data -s src/QbAutopost.Api -o deploy/migrations.sql`.
- [ ] **Step 10: Run the tests** (same filter, then `MigrationScriptTests`, `MigrationTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green (Data tests need Docker; record skips in the tracker).
- [ ] **Step 11: Tracker row T-1703 and Session-log line.**
- [ ] **Step 12: Commit**

```bash
git add src/QbAutopost.Core/Companies/Company.cs src/QbAutopost.Core/Companies/CompanyContext.cs src/QbAutopost.Core/Companies/ICompanyStore.cs src/QbAutopost.Core/Companies/ICompanyResolver.cs src/QbAutopost.Core/SavedRequests/SavedRequestModels.cs src/QbAutopost.Core/Extraction/StatementExtraction.cs src/QbAutopost.Core/SavedRequests/Audit/AuditActions.cs src/QbAutopost.Data/ deploy/grants.sql deploy/migrations.sql tests/QbAutopost.Api.Tests/TestSupport/FakeCompanyStore.cs tests/QbAutopost.Data.Tests/CompanyStoreContract.cs tests/QbAutopost.Data.Tests/FakeCompanyStoreTests.cs tests/QbAutopost.Data.Tests/EfCompanyStoreTests.cs tests/QbAutopost.Data.Tests/MultiCompanyMigrationTests.cs docs/tracker.md
git commit -m "T-1703: companies table, company_id columns, MultiCompany migration, EfCompanyStore"
```

---

### Task T-1704: `Companies` settings, `CompanyBootstrap`, `CompanyResolver`, `CompanyDataFolder`

**Files:**
- Modify: `Configuration/AppSettings.cs` (`AppSettings.Companies`, `CompaniesSettings`, `ResolvePaths`), `Program.cs`, `tests/QbAutopost.Api.Tests/TestSupport/ApiFactory.cs`
- Create: `Configuration/CompaniesOptionsValidator.cs`, `Companies/CompanyBootstrap.cs`, `Companies/CompanyResolver.cs`, `Companies/CompanyDataFolder.cs`
- Modify (**shared file, see below**): `src/QbAutopost.Api/appsettings.json`
- Test: `tests/QbAutopost.Api.Tests/Companies/{CompaniesSettingsTests,CompanyDataFolderTests,CompanyResolverTests,CompanyBootstrapTests}.cs`, `tests/QbAutopost.Data.Tests/Api/MultiCompanyUpgradeTests.cs`

**Interfaces:**
- Consumes: `ICompanyStore`, `ICompanyResolver`, `CompanyContext`, `NewDefaultCompany`, `DefaultCompanyResult` (T-1703); `CompanyFolderScanner.ParseFolderName` (T-1702).
- Produces:

```csharp
public sealed class CompaniesSettings
{
    public string RootFolder { get; set; } = "";
    public string DataFolder { get; set; } = "data/companies";
    public string RulesTemplate { get; set; } = "data/rules.json";
    public string ScheduledSync { get; set; } = ScheduledSyncModes.WaitingOnly;
    public bool Configured => !string.IsNullOrWhiteSpace(RootFolder);
}
public static class ScheduledSyncModes { public const string WaitingOnly = "waiting-only", All = "all", Off = "off"; }
public sealed class CompaniesOptionsValidator : IValidateOptions<AppSettings>;
public sealed class CompanyDataFolder(IOptions<AppSettings> settings)
{
    public string FolderOf(int companyId);      // <DataFolder>/<id>
    public string RulesFileOf(int companyId);   // <DataFolder>/<id>/rules.json
    public string LedgerFileOf(int companyId);  // <DataFolder>/<id>/ledger.json
    /// <summary>Creates the folder; copies RulesTemplate to rules.json and writes Ledger.Empty to ledger.json, each only if absent.</summary>
    public void Prepare(int companyId);
}
public sealed class CompanyBootstrap(ICompanyStore store, IOptions<AppSettings> settings, CompanyResolver resolver, IClock clock, ILogger<CompanyBootstrap> log)
{
    public Task<DefaultCompanyResult> RunAsync(CancellationToken ct);   // idempotent; sets resolver's default id
}
public sealed class CompanyResolver(IOptions<AppSettings> settings, CompanyDataFolder folders, ICompanyStore? store = null) : ICompanyResolver
{
    public void UseDefaultId(int id);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// CompaniesSettingsTests (pattern of Configuration/AppSettingsTests)
[Fact] public void Should_DefaultToWaitingOnlyAndDataCompanies_When_SectionAbsent()
[Fact] public void Should_ResolveDataFolderAndTemplate_When_PathsAreRelative()      // ResolvePaths(root) makes both absolute; RootFolder untouched
[Theory][InlineData("relative\\folder")][InlineData("data/companies")]
public void Should_FailStartup_When_RootFolderIsNotAbsolute(string root)             // message names Companies:RootFolder
[Fact] public void Should_FailStartup_When_ScheduledSyncIsUnknown()                  // "sometimes" → names Companies:ScheduledSync and the three values
[Fact] public void Should_FailStartup_When_RootFolderSetAndRulesTemplateMissing()

// CompanyDataFolderTests (TempDir)
[Fact] public void Should_CopyTemplateAndCreateEmptyLedger_When_Prepared()
[Fact] public void Should_KeepEditedRules_When_PreparedAgain()                        // copied once; later template edits do not propagate

// CompanyResolverTests
[Fact] public void Should_UseConfiguredFiles_When_ResolvingTheDefault()   // Default: FilePath = Company:FilePath, RulesFile = Company:RulesFile, LedgerFile = Paths:Ledger, IsDefault
[Fact] public async Task Should_UseDataFolderFiles_When_ResolvingAScannedCompany()   // RulesFile = <DataFolder>/<id>/rules.json
[Fact] public async Task Should_AutoAssignDefault_When_RootFolderIsEmpty()           // MultiCompany false → Default
[Fact] public async Task Should_AutoAssignTheOnlyActiveCompany_When_OneIsActive()
[Fact] public async Task Should_AutoAssignNothing_When_TwoCompaniesAreActive()
[Fact] public async Task Should_ReportSingleCompany_When_NoDatabase()                // store null → MultiCompany false, GetAsync(other) null

// CompanyBootstrapTests (FakeCompanyStore)
[Fact] public async Task Should_CreateDefaultFromCompanySettings_When_NoCompanyExists()
// DisplayName = Company:Name, FilePath = Company:FilePath, Active, Approved
[Fact] public async Task Should_SetFolderNameAndCode_When_DefaultFileLiesUnderRootFolder()
// root/18_Takoma Park Grocery Store Inc/Takoma.qbw → FolderName "18_Takoma Park Grocery Store Inc", Code "18"
[Fact] public async Task Should_CopyNoFiles_When_CreatingTheDefault()                // DataFolder stays empty (conflict 5)
[Fact] public async Task Should_DoNothing_When_RunTwice()

// MultiCompanyUpgradeTests (Data.Tests/Api, real PostgreSQL, pattern of HostBootTests)  — A-9
[PostgresFact] public async Task Should_AssignEveryOldRowToTheDefaultCompany_When_HostStartsAfterUpgrade()
// migrate; raw SQL INSERT one saved_requests, one statement_extractions, one qb_list_syncs row with company_id NULL;
// boot ApiFactory.WithPostgres → all three rows company_id = default id; saved_requests.company_source = 'migrated'
[PostgresFact] public async Task Should_LeaveLaterUnassignedRows_When_HostRestarts()
// after first boot, insert another null-company row; reboot → still null, companies count 1
[PostgresFact] public async Task Should_AnswerTheExistingDashboardAsBefore_When_NoCompanyParameterIsSent()
// GET /api/v2/saved-requests and /api/v2/quickbooks/lists without company → 200, same shape as before
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tests/QbAutopost.Api.Tests --filter "FullyQualifiedName~Companies."` and `dotnet test tests/QbAutopost.Data.Tests --filter "FullyQualifiedName~MultiCompanyUpgradeTests"` — FAIL.
- [ ] **Step 3: Settings + validator.** `ResolvePaths` resolves `DataFolder` and `RulesTemplate` against the content root; `RootFolder` must already be absolute (`Path.IsPathFullyQualified`) when set. Register the validator next to `ListSyncOptionsValidator` in `Program.cs`.
- [ ] **Step 4: `CompanyDataFolder`, `CompanyResolver`, `CompanyBootstrap`** as in Interfaces. `MultiCompany` = `RootFolder` set **and** an `ICompanyStore` registered (no database → off, spec §4.4). A non-default context with `FilePath` null (ambiguous) uses `""` and carries its state, so callers refuse it. Bootstrap computes `FolderName` only when `Path.GetDirectoryName(Company:FilePath)`'s parent equals `RootFolder` (OrdinalIgnoreCase, full paths).
- [ ] **Step 5: Wire `Program.cs`.** Singletons `CompanyDataFolder`, `CompanyResolver` (+ `ICompanyResolver` → same instance; store resolved with `sp.GetService<ICompanyStore>()`), `CompanyBootstrap`. Startup block: `await CompanyBootstrap.RunAsync(...)` **before** `DbQbListsSource.InitializeAsync`; a database error is logged at Error and does not stop startup (as the lists load). The `DatabaseGate` recovery callback also calls `RunAsync` before `lists.EnsureLoadedAsync`. `ApiFactory`: when not on PostgreSQL register a `FakeCompanyStore` (`public FakeCompanyStore Companies { get; }`) as `ICompanyStore`, and set `Companies:DataFolder` to `Dir.Combine("data","companies")` and `Companies:RulesTemplate` to `Fixtures.SampleRules`.
- [ ] **Step 6: `appsettings.json` — shared file.** Add the `Companies` section of spec §4.1 with `"RootFolder": ""` (shipped off). **`src/QbAutopost.Api/appsettings.json` already has someone else's uncommitted changes**: before editing run `git diff src/QbAutopost.Api/appsettings.json`; stage only this hunk with `git add -p src/QbAutopost.Api/appsettings.json` (answer `n` to every other hunk) or ask the owner to commit theirs first. If neither is possible, leave the file out of the commit and record that in the tracker row.
- [ ] **Step 7: Run the tests** — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 8: Tracker row T-1704 and Session-log line** (note how `appsettings.json` was staged).
- [ ] **Step 9: Commit**

```bash
git add src/QbAutopost.Api/Configuration/AppSettings.cs src/QbAutopost.Api/Configuration/CompaniesOptionsValidator.cs src/QbAutopost.Api/Companies/CompanyBootstrap.cs src/QbAutopost.Api/Companies/CompanyResolver.cs src/QbAutopost.Api/Companies/CompanyDataFolder.cs src/QbAutopost.Api/Program.cs tests/QbAutopost.Api.Tests/TestSupport/ApiFactory.cs tests/QbAutopost.Api.Tests/Companies/ tests/QbAutopost.Data.Tests/Api/MultiCompanyUpgradeTests.cs docs/tracker.md
git add -p src/QbAutopost.Api/appsettings.json   # only the Companies hunk
git commit -m "T-1704: Companies settings, CompanyBootstrap, CompanyResolver, CompanyDataFolder"
```

---

### Task T-1705: `GET /companies`, `POST /companies/scan`, `PATCH /companies/{id}`

**Files:**
- Create: `Endpoints/V2/CompanyEndpoints.cs`, `Companies/CompanyProblems.cs`
- Modify: `Endpoints/ApiRoutes.cs` (`V2Routes`, `ScopeFor`), `Program.cs` (`v2.MapCompanyEndpoints()`, `ICompanyFolderSource`/`CompanyFolderScanner` singletons), `wwwroot/openapi-v2.json`, `src/QbAutopost.Core/SavedRequests/ISavedRequestV2Store.cs`, `src/QbAutopost.Data/EfSavedRequestStore.V2.cs`, `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.V2.cs`
- Test: `tests/QbAutopost.Api.Tests/Companies/CompanyEndpointsTests.cs`, `tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs`, `tests/QbAutopost.Data.Tests/ListV2StoreContract.cs` (+1)

**Interfaces:**
- Consumes: `ICompanyStore`, `Company`, `CompanyText` (T-1703); `CompanyFolderScanner.Scan/Reconcile`, `CompanyRootUnavailableException` (T-1702); `CompanyDataFolder.Prepare`, `CompaniesSettings` (T-1704).
- Produces:

```csharp
public sealed record CompanyRefView(int Id, string? Code, string DisplayName);
public sealed record CompanyListsView(DateTime? SyncedUtc, string? Outcome);
public sealed record CompanyView(int Id, string? Code, string DisplayName, string? FolderName, string? FileName, string? FilePath,
    string State, IReadOnlyList<string> CandidateFiles, string Access, DateTime? AccessCheckedUtc, string? AccessMessage,
    string? QbCompanyName, string? QbLegalName, IReadOnlyList<string> MatchNames, bool IsDefault, int RowVersion,
    CompanyListsView? Lists, int WaitingCount);
public sealed record CompaniesView(string RootFolder, bool RootConfigured, bool RootReachable, DateTime? LastScanUtc, IReadOnlyList<CompanyView> Items);
public sealed record CompanyScanView(int Added, int Updated, int Missing, int Ambiguous, IReadOnlyList<string> NoCompanyFile, IReadOnlyList<CompanyView> Items);
public static class CompanyProblems   // RFC 7807 via Results.Problem; Type is the code
{
    public static IResult RootUnavailable(string rootFolder);                 // 503 company-root-unavailable, detail names the path
    // T-1708 adds Required, NotReady, OpenElsewhere, FileUnavailable; T-1710 adds QuickBooksNotOpen
}
// ISavedRequestV2Store: Task<IReadOnlyDictionary<int, int>> PendingByCompanyAsync(CancellationToken ct);
// FakeSavedRequestStore: void SetCompany(long id, int? companyId, CompanySource? source)   (test helper)
public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder v2);
```

- [ ] **Step 1: Write the failing tests** (`CompanyEndpointsTests`, `ApiFactory` + `WithSettings(Companies:RootFolder = TempDir root)`; the two folders of A-1 created as real files):

```csharp
[Fact] public async Task Should_ReturnTwoActiveCompaniesWithCodes_When_RootHoldsTwoCompanyFolders()   // A-1
// admin POST /api/v2/companies/scan → 200 {added 2}; items have codes "18","440", state "active", access "unchecked",
// folderName "18_Takoma Park Grocery Store Inc", "440_S & A Convenience Market LLC"; Companies.AuditEvents has companies-scanned
[Fact] public async Task Should_AddNothing_When_ScannedASecondTime()                                  // A-1: added 0
[Fact] public async Task Should_CopyRulesTemplateIntoCompanyFolder_When_CompanyIsAdded()             // <DataFolder>/<id>/rules.json equals the template
[Fact] public async Task Should_MarkMissingAndKeepStatements_When_SubfolderIsRemoved()                // A-2: state "missing"; a statement set to it via SetCompany keeps it
[Fact] public async Task Should_ListBothCandidatesAndActivateOnPatch_When_FolderHoldsTwoQbw()         // A-3
// state "ambiguous", candidateFiles 2; PATCH {filePath: candidate[1]} If-Match "<rv>" → 200 state "active", ETag new rv; audit company-updated
[Fact] public async Task Should_Answer412_When_PatchIfMatchIsStale()
[Fact] public async Task Should_Answer428_When_PatchHasNoIfMatch()
[Fact] public async Task Should_Answer400_When_FilePathIsNotACandidate()
[Fact] public async Task Should_Answer503RootUnavailable_When_RootFolderDoesNotExist()                // type company-root-unavailable, detail has the path; no company changed
[Fact] public async Task Should_ReportNotConfigured_When_RootFolderIsEmpty()                          // GET → rootConfigured false, items [default]
[Fact] public async Task Should_CountPendingStatements_When_ListingCompanies()                        // waitingCount
[Fact] public async Task Should_Answer404_When_PatchingUnknownCompany()
[Theory] viewer GET 200, viewer POST scan 403, editor PATCH 403, admin 200                           // R-18 acceptance

// V2ScopeMatrixTests: branch for "/companies" (v2-only: v1 form admin; GET lists:read, scan/patch requests:admin);
// closed cases: DELETE /api/v2/companies/1, GET /api/v2/companies/scan, POST /api/v2/companiesx
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tests/QbAutopost.Api.Tests --filter "FullyQualifiedName~CompanyEndpointsTests|FullyQualifiedName~V2ScopeMatrixTests"` — FAIL.
- [ ] **Step 3: Routes and scopes.** `V2Routes` += `("GET","/companies")`, `("POST","/companies/scan")`, `("PATCH","/companies/{id}")`. In `ScopeFor`, before the `/users` branch and only when the original path starts with `V2Prefix + "/"`: `GET /companies` → `ListsRead`; `POST /companies/scan` and `PATCH /companies/{id}` → `RequestsAdmin`; anything else under `/companies` → `Admin`. `IsMutating` already treats unlisted mutating routes as audited.
- [ ] **Step 4: Handlers.** GET: `RootReachable` = `Directory.Exists(RootFolder)`; `LastScanUtc` from the store; `Lists` from `IQbListsSource` for the default company only, null for others (`// T-1709`); `WaitingCount` from `PendingByCompanyAsync`. Scan: `RootFolder` empty → `RootUnavailable("Companies:RootFolder is not set")`; `CompanyRootUnavailableException` → `RootUnavailable(path)`, nothing applied; else `Reconcile(scan, known)` → `ApplyScanAsync(plan, caller.Actor, now)` → `Prepare(id)` for each `AddedIds`. PATCH: `If-Match` handled with the same helper `PUT /saved-requests/{id}` uses (428 / 412 `edited-by-someone-else`); `matchNames` trimmed, blanks dropped, at most 50 names of at most 200 characters else 400 `invalid-request`. Every company answer sets `ETag: "<rowVersion>"`. Endpoints are `.RequireDatabaseWhenConfigured()`; without a store GET answers `rootConfigured` from settings and the default company only, scan/PATCH answer 503 `company-root-unavailable` ("needs the database").
- [ ] **Step 5: OpenAPI.** `openapi-v2.json`: three operations with summary, `x-scope` as `ScopeFor` enforces, schemas `Company`, `CompanyList`, `CompanyScan`, `CompanyPatch`, enums `CompanyState`, `CompanyAccess`.
- [ ] **Step 6: Run the tests** (+ `OpenApiV2DriftTests`, `V2RoutingTests`, `V1ContractPinTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 7: Tracker row T-1705 and Session-log line.**
- [ ] **Step 8: Commit**

```bash
git add src/QbAutopost.Api/Endpoints/V2/CompanyEndpoints.cs src/QbAutopost.Api/Companies/CompanyProblems.cs src/QbAutopost.Api/Endpoints/ApiRoutes.cs src/QbAutopost.Api/Program.cs src/QbAutopost.Api/wwwroot/openapi-v2.json src/QbAutopost.Core/SavedRequests/ISavedRequestV2Store.cs src/QbAutopost.Data/EfSavedRequestStore.V2.cs tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.V2.cs tests/QbAutopost.Api.Tests/Companies/CompanyEndpointsTests.cs tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs tests/QbAutopost.Data.Tests/ListV2StoreContract.cs docs/tracker.md
git commit -m "T-1705: companies list, scan and patch routes"
```

---

### Task T-1706: Gateway — target company file per call, wrong-company exception

**Files:**
- Modify: `src/QbAutopost.Core/Abstractions/IQbGateway.cs`, `src/QbAutopost.Core/Gateway/ResilientQbGateway.cs`, `src/QbAutopost.QuickBooks/QbGateway.cs`, `src/QbAutopost.QuickBooks/QbSession.cs`, `QuickBooks/{LoggingQbGateway,SimulatedQbGateway,UnconfiguredQbGateway,QbConnection}.cs`, `tests/QbAutopost.Api.Tests/TestSupport/FakeQbGateway.cs`
- Create: `src/QbAutopost.Core/Gateway/TargetedQbGateway.cs`, `src/QbAutopost.QuickBooks/QbSessionErrors.cs`
- Test: `tests/QbAutopost.Core.Tests/Gateway/ResilientQbGatewayTests.cs` (+), `tests/QbAutopost.Api.Tests/QuickBooks/{TargetedGatewayTests,QbSessionErrorsTests}.cs`, `tests/QbAutopost.Api.Tests/QuickBooks/LoggingQbGatewayTests.cs` (+)

**Interfaces:**
- Consumes: `CompanyContext` (T-1703).
- Produces:

```csharp
public sealed record QbTarget(string CompanyFile);
public interface IQbGateway
{
    Task<string> ProcessAsync(string qbxml, CancellationToken ct);              // the configured default file, unchanged
    Task<string> CurrentCompanyFileAsync(CancellationToken ct);
    // Default implementations refuse (nothing sent), so the test doubles that serve one file need no change:
    Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) => Task.FromException<string>(
        new QuickBooksUnavailableException($"this gateway serves only the configured company file, not {target.CompanyFile}"));
    Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) => /* same refusal */;
}
public class QuickBooksUnavailableException(string message, Exception? inner = null) : Exception(message, inner);   // unsealed
public sealed class QuickBooksWrongCompanyException(string neededFile, string? openFile, string message, Exception? inner = null)
    : QuickBooksUnavailableException(message, inner) { public string NeededFile { get; } public string? OpenFile { get; } }
public sealed class QuickBooksCompanyFileUnavailableException(string neededFile, string sdkMessage, Exception? inner = null)
    : QuickBooksUnavailableException(..., inner) { public string NeededFile { get; } public string SdkMessage { get; } }
public static class QbGatewayTargets
{
    /// <summary>A gateway whose untargeted calls go to <paramref name="target"/>; the gateway itself when target is null.</summary>
    public static IQbGateway For(this IQbGateway gateway, QbTarget? target);
}
// CompanyContext gains: public QbTarget? Target => IsDefault ? null : new QbTarget(FilePath);
public static class QbSessionErrors   // QbAutopost.QuickBooks, no COM types, not Windows-only
{
    /// <summary>SPEC-GAP T-1707: HRESULTs BeginSession returns when QuickBooks has another company file open. Empty until verified.</summary>
    public static IReadOnlySet<int> WrongCompanyFile { get; }
    public static QuickBooksUnavailableException FromBeginSession(int hresult, string companyFile, string appName, string description, Exception inner, IReadOnlySet<int>? wrongCompanyCodes = null);
}
// SimulatedQbGateway(SimulatedQuickBooks company, string defaultCompanyFile = CompanyFile): SimulatedQuickBooks CompanyFor(string file)
// FakeQbGateway: const string DefaultCompanyFile = @"C:\fake\Tropicana.QBW"; string? OpenCompanyFile { get; set; };
//   SimulatedQuickBooks CompanyFor(string file); IReadOnlyList<string?> Targets (per recorded request; null = untargeted);
//   int Refusals (calls refused because another file is open)
```

- [ ] **Step 1: Write the failing tests**

```csharp
// ResilientQbGatewayTests (Core; ScriptedGateway gains targeted overrides recording the target)
[Fact] public async Task Should_PassTheTargetThrough_When_CallIsTargeted()
[Fact] public async Task Should_SerialiseOnOneLock_When_TwoCompaniesCallAtOnce()   // second call starts only after the first ends
[Fact] public async Task Should_NotRetry_When_WrongCompanyFileIsOpen()             // ScriptedGateway throws QuickBooksWrongCompanyException once → exactly 1 call, exception surfaces
[Fact] public async Task Should_NotRetry_When_CompanyFileIsUnavailable()
[Fact] public async Task Should_RetryOnce_When_TargetedSessionCouldNotOpen()      // plain QuickBooksUnavailableException → 2 calls (as untargeted today)

// QbSessionErrorsTests (Api.Tests; the pure mapper)
[Fact] public void Should_ReturnWrongCompany_When_HResultIsInTheList()            // NeededFile = file, OpenFile null
[Fact] public void Should_ReturnPlainUnavailable_When_HResultIsNotInTheList()     // exact type QuickBooksUnavailableException, message has 0xXXXXXXXX
[Fact] public void Should_StartEmpty_When_NotYetVerifiedOnTheServer()              // QbSessionErrors.WrongCompanyFile is empty (SPEC-GAP T-1707)

// TargetedGatewayTests (Api.Tests)
[Fact] public async Task Should_ThrowWrongCompanyAndSendNothing_When_AnotherFileIsOpen()
// ResilientQbGateway(LoggingQbGateway(fake)); fake.OpenCompanyFile = @"C:\fake\SA.QBW";
// ProcessAsync(new QbTarget(@"C:\fake\Takoma.QBW"), query) → QuickBooksWrongCompanyException{NeededFile Takoma, OpenFile SA}; fake.Requests empty; fake.Refusals 1
[Fact] public async Task Should_AnswerFromEachCompany_When_SimulatedGatewayIsTargeted()   // lists seeded in CompanyFor(A) are not in CompanyFor(B)
[Fact] public async Task Should_UseTheSeededCompany_When_TargetIsTheDefaultFile()
[Fact] public async Task Should_ReachTheTarget_When_UntargetedCallGoesThroughFor()      // gateway.For(target).ProcessAsync → fake.Targets [target]
[Fact] public async Task Should_RefuseTargetedCall_When_GatewayServesOneFile()          // a stub with no override → QuickBooksUnavailableException, nothing sent
[Fact] public async Task Should_LogTheCompanyFile_When_CallIsTargeted()                 // LoggingQbGatewayTests: line names the target file
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test --filter "FullyQualifiedName~ResilientQbGatewayTests|FullyQualifiedName~TargetedGatewayTests|FullyQualifiedName~QbSessionErrorsTests|FullyQualifiedName~LoggingQbGatewayTests"` — FAIL.
- [ ] **Step 3: Core.** `IQbGateway` as above; exceptions; `TargetedQbGateway(IQbGateway inner, QbTarget target) : IQbGateway` maps untargeted calls to `inner`'s targeted ones. `ResilientQbGateway` implements both targeted members through the same `RunWithRetryAsync`/`_lock`; `CanRetry` gets `QuickBooksWrongCompanyException => false` and `QuickBooksCompanyFileUnavailableException => false` before the `QuickBooksUnavailableException` arm.
- [ ] **Step 4: QuickBooks project.** `QbGateway` targeted members open `QbSession.Open(options.AppName, target.CompanyFile, options.Trace)`. `QbSession.Open`'s `catch (COMException ex)` around `BeginSession` throws `QbSessionErrors.FromBeginSession(ex.HResult, companyFile, appName, Describe(ex), ex)` (the OpenConnection2 failure keeps today's message). The message keeps today's wording ("could not open a QuickBooks session for '…' (0x…: …); is QuickBooks running with the certificate accepted for …?").
- [ ] **Step 5: Api gateways.** `LoggingQbGateway` targeted members log `"QuickBooks call {CallNo}: sending {Requests} to {CompanyFile} …"`. `UnconfiguredQbGateway` targeted members throw its message. `SimulatedQbGateway` keeps a `ConcurrentDictionary<string, SimulatedQuickBooks>(StringComparer.OrdinalIgnoreCase)`; the default file maps to the seeded company; `CurrentCompanyFileAsync(target)` returns the target. `QbConnection.Create` passes `settings.Company.FilePath` (or `SimulatedQbGateway.CompanyFile` when empty) as the default file.
- [ ] **Step 6: `FakeQbGateway`.** Targeted and untargeted calls resolve the needed file (`target?.CompanyFile ?? DefaultCompanyFile`); when `OpenCompanyFile` is set and differs (OrdinalIgnoreCase) the call increments `Refusals` and throws `QuickBooksWrongCompanyException(needed, OpenCompanyFile, …)` **before** recording anything; otherwise it records the request and its target and answers from `CompanyFor(needed)` (the default file → `Company`). Existing behaviour with `OpenCompanyFile` null is unchanged.
- [ ] **Step 7: Run the tests** — PASS; `dotnet build -warnaserror`; `dotnet test` — all green (every existing test double compiles through the default interface members).
- [ ] **Step 8: Tracker row T-1706 (+ Questions row: wrong-company HRESULT list empty until T-1707) and Session-log line.**
- [ ] **Step 9: Commit**

```bash
git add src/QbAutopost.Core/Abstractions/IQbGateway.cs src/QbAutopost.Core/Gateway/ResilientQbGateway.cs src/QbAutopost.Core/Gateway/TargetedQbGateway.cs src/QbAutopost.Core/Companies/CompanyContext.cs src/QbAutopost.QuickBooks/QbGateway.cs src/QbAutopost.QuickBooks/QbSession.cs src/QbAutopost.QuickBooks/QbSessionErrors.cs src/QbAutopost.Api/QuickBooks/LoggingQbGateway.cs src/QbAutopost.Api/QuickBooks/SimulatedQbGateway.cs src/QbAutopost.Api/QuickBooks/UnconfiguredQbGateway.cs src/QbAutopost.Api/QuickBooks/QbConnection.cs tests/QbAutopost.Api.Tests/TestSupport/FakeQbGateway.cs tests/QbAutopost.Core.Tests/Gateway/ResilientQbGatewayTests.cs tests/QbAutopost.Api.Tests/QuickBooks/TargetedGatewayTests.cs tests/QbAutopost.Api.Tests/QuickBooks/QbSessionErrorsTests.cs tests/QbAutopost.Api.Tests/QuickBooks/LoggingQbGatewayTests.cs docs/tracker.md
git commit -m "T-1706: per-call company file through the one gateway lock; wrong-company exception"
```

---

### Task T-1707 [server]: Verify the SDK's wrong-company behaviour on the QuickBooks server

**Files:**
- Create: `docs/testing/T-1707.server-checklist.md`

**Interfaces:**
- Consumes: `QbSessionErrors.WrongCompanyFile` (T-1706).
- Produces: the verified HRESULT list (or the finding that it cannot be told apart, which switches spec §4.7 to `company-file-unavailable`).

- [ ] **Step 1: Write the checklist** in the T-1603 style (intro: tick in order, stop on a mismatch, write what you saw, never write a password or key; run with a recent backup). Items:
  1. **Two approved files.** Admin opens Takoma's `.qbw`, accepts the *QbAutopost* certificate with "Yes, always; allow access even if QuickBooks is not running" and a login user; repeats for S & A. Record both full paths.
  2. **Wrong file open.** With Takoma open in QuickBooks, run `scripts/qb-server-check.ps1`-style probe (or `POST /api/v2/companies/{S&A id}/check`) for S & A. Record the HRESULT (`0x…`) and message from the API log line `could not open a QuickBooks session for '…' (0x…: …)`.
  3. **Same with another pair** (S & A open, Takoma requested): same HRESULT? Record it.
  4. **Not distinguishable?** Compare with the HRESULTs of: QuickBooks closed + file not approved; file path wrong; a modal dialog open in QuickBooks. If the wrong-file code also appears for any of these, write "NOT DISTINGUISHABLE".
  5. **Background open.** Close QuickBooks; post a small Takoma statement: it posts; record the time from the log (`BeginSession` to `session open`).
  6. **Largest file.** Repeat item 5 for the largest company file; record seconds and compare with `Api:SyncPostTimeoutSeconds` and `QuickBooks:BusyTimeoutSeconds`.
  7. **Open-file name.** Whether `GetCurrentCompanyFileName` can be read when `BeginSession` fails (expected: no ticket, so no); record.
  8. **Unapproved file with QuickBooks closed.** Does `BeginSession` fail at once or show a dialog? Record the HRESULT or the dialog text and how long the API waited.
  9. **Result.** Fill: "Wrong-company HRESULTs: ____ / NOT DISTINGUISHABLE". The agent then sets `QbSessionErrors.WrongCompanyFile` to these codes (or, if not distinguishable, maps every targeted `BeginSession` failure to `QuickBooksCompanyFileUnavailableException`) in a follow-up commit under T-1707.
- [ ] **Step 2: Verify docs.** `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~OperatorDocsTests"` — PASS (versioned routes only).
- [ ] **Step 3: Tracker row T-1707 → `ready-for-human`, Session-log line; stop here.**
- [ ] **Step 4: Commit**

```bash
git add docs/testing/T-1707.server-checklist.md docs/tracker.md
git commit -m "T-1707: server checklist for the wrong-company SDK error and background open"
```

---

### Task T-1708: `CompanyContext` through posting, list sync and validation

**Files:**
- Modify: `Jobs/DirectPostRunner.cs`, `Jobs/SyncPostRunner.cs`, `SavedRequests/SavedRequestPoster.cs`, `SavedRequests/SavedPostOutcome.cs`, `SavedRequests/SavedRequestValidator.cs`, `SavedRequests/SavedRequestIntake.cs` (revalidate), `Endpoints/SavedRequestEndpoints.cs` (validate/post problem mapping), `Endpoints/SyncPostResponses.cs`, `Companies/CompanyProblems.cs`, `Program.cs`, `src/QbAutopost.Core/Pipeline/{QbListSync,PipelineOptions,ListSyncRun}.cs`, `src/QbAutopost.Core/Api/DirectPostResult.cs`, `src/QbAutopost.Core/SavedRequests/{PostEnding,AttemptOutcomes}.cs`
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/V2CompanyPostTests.cs`, `tests/QbAutopost.Core.Tests/SavedRequests/AttemptOutcomesTests.cs` (+), `tests/QbAutopost.Core.Tests/Pipeline/QbListSyncTests.cs` (+)

**Interfaces:**
- Consumes: `CompanyContext`, `ICompanyResolver` (T-1703/T-1704); `QbTarget`, `For`, `QuickBooksWrongCompanyException`, `QuickBooksCompanyFileUnavailableException`, `FakeQbGateway.OpenCompanyFile` (T-1706).
- Produces:

```csharp
// Core/Api/DirectPostResult.cs
public sealed record CompanyFileRefusal(string Type, string NeededFile, string? OpenFile, string? SdkMessage); // Type: company-file-open-elsewhere | company-file-unavailable
// DirectPostResult gains: public CompanyFileRefusal? CompanyRefusal { get; init; }
// DirectPostRunner (gains ICompanyResolver resolver)
public virtual DirectPlan Plan(DirectRequest request);                              // = Plan(request, resolver.Default)
public virtual DirectPlan Plan(DirectRequest request, CompanyContext company);
public DirectPostResult Begin(string batchId, DirectRequest request, DirectPlan plan, bool dryRun, CompanyContext? company = null);
public Task<DirectPostResult> RunAsync(string batchId, DirectRequest request, DirectPlan plan, CancellationToken ct, CompanyContext? company = null);
// SyncPostRunner
public sealed record SyncPost(SyncPostOutcome Outcome, IReadOnlyList<string> Errors, SyncPostFindings? Findings, SyncPostResult? Result, bool Unavailable, CompanyFileRefusal? CompanyRefusal = null);
public Task<SyncPost> RunAsync(SyncPostRequest request, CancellationToken ct, Action? onSending = null, IReadOnlySet<int>? notPosted = null, CompanyContext? company = null);
// PostEndingKind gains CompanyFileOpenElsewhere, CompanyFileUnavailable
//   AttemptOutcomes: CompanyFileOpenElsewhere → (Unavailable, Pending, 409); CompanyFileUnavailable → (Unavailable, Pending, 503)
// SavedPostKind gains CompanyRequired, CompanyNotReady (no claim, no attempt)
// SavedRequestValidator: Validate/ValidateBoth gain CompanyContext? company (null = default); const string CompanyRequired = "company required"
// QbListSync
public Task<ListSyncCompletion> SyncWithRunAsync(CompanyContext company, ListSyncTrigger trigger, string? requestedBy, CancellationToken ct);
// ListSyncReasons gains CompanyFileOpenElsewhere = "company-file-open-elsewhere"
// CompanyProblems gains: Required(string publicRef) 409 company-required; NotReady(CompanyContext c) 409 company-not-ready + extensions state, access;
//   OpenElsewhere(CompanyFileRefusal r) 409 company-file-open-elsewhere + openFile, neededFile; FileUnavailable(CompanyFileRefusal r) 503 company-file-unavailable + neededFile
```

- [ ] **Step 1: Write the failing tests** (`V2CompanyPostTests`: `FakeCompanyStore` with two companies whose `FilePath`s are `C:\fake\Takoma.QBW` and `C:\fake\SA.QBW`, access approved, `CompanyDataFolder.Prepare` run for each; statements saved through the v2 route and pointed at a company with `Store.SetCompany`):

```csharp
[Fact] public async Task Should_PostToTheStatementsCompanyFile_When_CompanyIsApproved()
// post Takoma statement → 200 posted; every fake.Targets entry == Takoma file; batch in <DataFolder>/<takoma>/ledger.json; Paths:Ledger unchanged
[Fact] public async Task Should_Answer409OpenElsewhereAndNotRetry_When_AnotherCompanyFileIsOpen()   // A-7
// fake.OpenCompanyFile = SA file; post Takoma → 409 type "company-file-open-elsewhere", openFile SA, neededFile Takoma;
// attempt outcome "unavailable"; fake.Writes empty; fake.Refusals == 1; request status "pending"
[Fact] public async Task Should_Answer409NotReadyWithoutASession_When_CompanyNeedsApproval()        // A-12 (routes half)
// access needs-approval → POST validate 409 company-not-ready {state "active", access "needs-approval"}; POST post same; no attempt; fake.Requests empty
[Fact] public async Task Should_Answer409NotReady_When_CompanyIsDisabledAfterValidation()         // Review Focus 3
[Fact] public async Task Should_PlanWithTheCompanysRules_When_Validating()
// Takoma's rules.json gains a vendor account that sample rules lack → validation of a Takoma statement has no held row; same payload on default holds it
[Fact] public async Task Should_PostAsBefore_When_StatementHasNoCompanyYet()                       // T-1712 marker path: null → default (conflict 14)
// Core
[Fact] public void Should_RecordUnavailableWith409_When_CompanyFileIsOpenElsewhere()               // AttemptOutcomesTests
[Fact] public void Should_RecordUnavailableWith503_When_CompanyFileIsUnavailable()
[Fact] public async Task Should_RecordSkippedOpenElsewhere_When_SyncFindsAnotherFileOpen()        // QbListSyncTests: row Outcome Skipped, Reason company-file-open-elsewhere; exception rethrown
[Fact] public async Task Should_LoadTheCompanysRules_When_SyncingAContext()                       // MissingInRules from the context's RulesFile
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test --filter "FullyQualifiedName~V2CompanyPostTests|FullyQualifiedName~AttemptOutcomesTests|FullyQualifiedName~QbListSyncTests"` — FAIL.
- [ ] **Step 3: `DirectPostRunner`.** `Plan(request, company)` loads `Rules.Load(company.RulesFile)` and `new LedgerStore(company.LedgerFile)`; lists stay `listsSource.Current` (`// T-1709: per-company lists`). `RunAsync(…, company)`: `var gateway = this.gateway.For(company?.Target)`; LiveDuplicateGate and the add go through it; `RecordInLedger` uses `company.LedgerFile`; `Describe` uses `company.DisplayName` for `CompanyFile`. In both the G4 catch and the add catch, `QuickBooksWrongCompanyException w` → `CompanyRefusal = new("company-file-open-elsewhere", w.NeededFile, w.OpenFile, null)`, `QuickBooksCompanyFileUnavailableException u` → `new("company-file-unavailable", u.NeededFile, null, u.SdkMessage)`; the rest of the ending (nothing posted, `Unavailable = true`, no ledger record) is unchanged. A null company = today's behaviour exactly.
- [ ] **Step 4: `SyncPostRunner`** passes `company` to `Plan`/`Begin`/`RunAsync` and copies `result.CompanyRefusal` into `SyncPost`. `SyncPostResponses.StatusOf`/`BodyOf`: a `CompanyRefusal` answers its problem (409/503) with the saved-request extra fields.
- [ ] **Step 5: `SavedRequestPoster.PostAsync`.** After NotFound and before the duplicate question: resolve `record.CompanyId is { } id ? await resolver.GetAsync(id) : resolver.Default` (`// T-1712: company-required hold`); a context with `!CanOpenSession` → `SavedPostKind.CompanyNotReady` (409, nothing claimed). Pass the context to `runner.RunAsync`. `EndingOf`: `post.CompanyRefusal` → `CompanyFileOpenElsewhere` / `CompanyFileUnavailable` before the `Unavailable` arm. The endpoint maps `CompanyNotReady` to `CompanyProblems.NotReady`, `CompanyRequired` to `CompanyProblems.Required` (used from T-1712).
- [ ] **Step 6: Validation.** `SavedRequestValidator` passes the context to `runner.Plan`; `RevalidateAsync` resolves the statement's company the same way and the validate endpoint answers `CompanyProblems.NotReady` before revalidating when `!CanOpenSession`.
- [ ] **Step 7: `QbListSync.SyncWithRunAsync(CompanyContext …)`.** Rules from `company.RulesFile`; gateway `For(company.Target)`; audit folder `Path.Combine(Path.GetDirectoryName(company.LedgerFile)!, AuditDir)` for non-default companies (default keeps `AuditFolder`); a `QuickBooksWrongCompanyException` records `Skipped` with `ListSyncReasons.CompanyFileOpenElsewhere` and rethrows. The existing overload is unchanged (default company). `PipelineOptions`: XML doc only ("the default company's files; v2 work uses `CompanyContext`"). `Program.cs`: `DirectPostRunner`, `SavedRequestPoster` resolve `ICompanyResolver`. `RulesEditor` and `CompanyFileValidator` unchanged (conflict 9).
- [ ] **Step 8: Run the tests** (+ `PostApiTests`, `V2PostTests`, `SyncPostApiTests`, `V1ContractPinTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 9: Tracker row T-1708 and Session-log line.**
- [ ] **Step 10: Commit**

```bash
git add src/QbAutopost.Api/Jobs/DirectPostRunner.cs src/QbAutopost.Api/Jobs/SyncPostRunner.cs src/QbAutopost.Api/SavedRequests/SavedRequestPoster.cs src/QbAutopost.Api/SavedRequests/SavedPostOutcome.cs src/QbAutopost.Api/SavedRequests/SavedRequestValidator.cs src/QbAutopost.Api/SavedRequests/SavedRequestIntake.cs src/QbAutopost.Api/Endpoints/SavedRequestEndpoints.cs src/QbAutopost.Api/Endpoints/SyncPostResponses.cs src/QbAutopost.Api/Companies/CompanyProblems.cs src/QbAutopost.Api/Program.cs src/QbAutopost.Core/Pipeline/QbListSync.cs src/QbAutopost.Core/Pipeline/PipelineOptions.cs src/QbAutopost.Core/Pipeline/ListSyncRun.cs src/QbAutopost.Core/Api/DirectPostResult.cs src/QbAutopost.Core/SavedRequests/PostEnding.cs src/QbAutopost.Core/SavedRequests/AttemptOutcomes.cs tests/QbAutopost.Api.Tests/SavedRequests/V2CompanyPostTests.cs tests/QbAutopost.Core.Tests/SavedRequests/AttemptOutcomesTests.cs tests/QbAutopost.Core.Tests/Pipeline/QbListSyncTests.cs docs/tracker.md
git commit -m "T-1708: CompanyContext through posting, list sync and validation; wrong-company 409"
```

---

### Task T-1709: Lists per company and the scheduled sweep

**Files:**
- Create: `src/QbAutopost.Core/Abstractions/IQbListsCatalog.cs`, `Jobs/CompanySweep.cs`, `QuickBooks/FileQbListsCatalog.cs`, `Endpoints/V2/CompanyParameter.cs`
- Modify: `QuickBooks/DbQbListsSource.cs`, `src/QbAutopost.Data/EfQbListsStore.cs`, `Endpoints/V2/QuickBooksListsV2Endpoints.cs`, `Endpoints/V2/CompanyEndpoints.cs` (lists per company), `Jobs/JobWorker.cs`, `Jobs/ListSyncSchedule.cs`, `Jobs/DirectPostRunner.cs` (lists from the catalog), `Program.cs`, `wwwroot/openapi-v2.json`
- Test: `tests/QbAutopost.Data.Tests/QbListSyncStoreTests.cs` (+), `tests/QbAutopost.Api.Tests/Api/V2CompanyListsTests.cs`, `tests/QbAutopost.Api.Tests/Jobs/{ScheduledCompanySweepTests,CompanySweepTests}.cs`

**Interfaces:**
- Consumes: `QbListSync.SyncWithRunAsync(CompanyContext …)`, `ListSyncReasons.CompanyFileOpenElsewhere` (T-1708); `ICompanyResolver`, `ICompanyStore` (T-1703/4); `PendingByCompanyAsync` (T-1705); `CompaniesSettings.ScheduledSync` (T-1704).
- Produces:

```csharp
public interface IQbListsCatalog
{
    IQbListsSource SourceFor(int companyId);   // memory reads only
    IQbListsStore StoreFor(int companyId);
}
// EfQbListsStore: every method gains `int companyId` first: RecordAsync(int, ListSyncRun, QbLists?, ct), LastAttemptAsync(int, ct),
//   ListAttemptsAsync(int, ListSyncQuery, ct), LoadCurrentAsync(int, ct), ImportIfEmptyAsync(int, QbLists, DateTime, ct); retention per company
// DbQbListsSource : IQbListsCatalog (+ IQbListsStore/IQbListsSource = the default company's view, as today)
// FileQbListsCatalog(FileQbLists file, ICompanyResolver resolver) : IQbListsCatalog   — default id only; any other id throws InvalidOperationException
// QbListSync gains IQbListsCatalog; the context overload records into StoreFor(company.Id) and reads SourceFor(company.Id)
public static class CompanySweep
{
    public static IReadOnlyList<Company> Select(IReadOnlyList<Company> companies, string mode,
        IReadOnlyDictionary<int, int> pendingByCompany, Func<int, ListSyncRun?> lastAttempt);
}
// Endpoints/V2/CompanyParameter.cs — shared by the lists routes (and the parsing rule T-1712/T-1713 reuse):
public static class CompanyParameter
{
    /// <summary>Absent → true with id null (the default company). Not a positive integer, repeated, or unknown → false with a 400 detail.</summary>
    public static Task<(bool Ok, CompanyContext? Company, string? Error)> ReadAsync(HttpContext http, ICompanyResolver resolver, JsonElement? body, CancellationToken ct);
}
// ListSyncSchedule: JoinOrStart(int companyId, sync, enqueue) joins only a flight for the same company;
//   InFlightFor(int companyId); BeginAttempt() still refuses while any flight is in flight (the sweep waits)
```

- [ ] **Step 1: Write the failing tests**

```csharp
// QbListSyncStoreTests (+, PostgreSQL)
[PostgresFact] public async Task Should_KeepCompaniesApart_When_TwoCompaniesRecord()      // A-8 store half: Load/Last/List for 1 unchanged after 2 records
[PostgresFact] public async Task Should_KeepNewestRowsPerCompany_When_HistoryExceedsLimit()

// V2CompanyListsTests (ApiFactory, two approved companies, fake CompanyFor(file) seeded with different accounts)
[Fact] public async Task Should_LeaveCompanyOneUntouched_When_CompanyTwoSyncs()            // A-8
// POST /api/v2/quickbooks/lists/sync?company=2 → 200; GET lists?company=1 unchanged; GET lists/syncs?company=1 has no new row
[Fact] public async Task Should_AcceptCompanyInTheBody_When_Syncing()                      // {"company": 2}
[Fact] public async Task Should_UseTheDefaultCompany_When_NoCompanyParameter()             // existing dashboard unchanged
[Fact] public async Task Should_Answer409NotReady_When_SyncingAnUnapprovedCompany()        // no session (fake.Requests empty)
[Fact] public async Task Should_Answer409OpenElsewhere_When_AnotherFileIsOpen()            // and a skipped row with reason company-file-open-elsewhere
[Theory][InlineData("abc")][InlineData("0")][InlineData("-5")]
public async Task Should_Answer400_When_CompanyParameterIsNotAnId(string value)           // Review Focus 4
[Fact] public async Task Should_Answer400_When_CompanyParameterIsRepeated()
[Fact] public async Task Should_Answer400_When_CompanyIsUnknown()

// CompanySweepTests (pure)
[Fact] public void Should_SelectWaitingOrNeverSynced_When_ModeIsWaitingOnly()
[Fact] public void Should_SelectEveryApprovedCompany_When_ModeIsAll()
[Fact] public void Should_SelectNothing_When_ModeIsOff()
[Fact] public void Should_NeverSelectUnapprovedOrInactive_When_AnyMode()                   // A-12 sweep half

// ScheduledCompanySweepTests (pattern of ScheduledListSyncTests, Workers helpers)
[Fact] public async Task Should_SkipOnlyThatCompany_When_ItsFileIsOpenElsewhere()
// fake.OpenCompanyFile = company 1's file → company 1 row skipped/company-file-open-elsewhere; company 2 row ok
[Fact] public async Task Should_NeverOpenTheFile_When_CompanyNeedsApproval()               // A-12: fake.Targets never contains it
[Fact] public async Task Should_SkipEveryCompanyAsNotOpen_When_QuickBooksIsNotRunning()    // FakeQbPresence false → one skipped quickbooks-not-open row per selected company
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test --filter "FullyQualifiedName~QbListSyncStoreTests|FullyQualifiedName~V2CompanyListsTests|FullyQualifiedName~CompanySweep"` — FAIL.
- [ ] **Step 3: Store and source.** `EfQbListsStore` filters every query on `company_id`; `DbQbListsSource` keeps `ConcurrentDictionary<int, State>`; `InitializeAsync` imports `qb-lists.json` into the **default** company only, then loads current lists and last attempt for every company in `ICompanyStore.ListAsync`; an unknown id reads `QbLists.Empty`. The existing `IQbListsSource`/`IQbListsStore` members act on the default id (so readiness, v1 `/quickbooks/lists`, `RulesEditor` and `CompanyFileValidator` are unchanged).
- [ ] **Step 4: Planning uses the company's lists.** `DirectPostRunner.Plan(request, company)` reads `catalog.SourceFor(company.Id).Current` (remove the `// T-1709` marker).
- [ ] **Step 5: Lists routes.** `CompanyParameter.ReadAsync` (query `company`, and for sync also body `{"company": n}`): absent → default; not a positive integer, repeated, or unknown → 400 `invalid-request`. A context with `!CanOpenSession` → `CompanyProblems.NotReady`. `JoinOrStart(company.Id, …)`; `SyncProblem` maps `QuickBooksWrongCompanyException` to `CompanyProblems.OpenElsewhere`. History and current lists read `catalog.SourceFor(id)`/`StoreFor(id)`. `CompanyView.Lists` now comes from `SourceFor(id).LastAttempt`/`Current.SyncedUtc` for every company. OpenAPI: the `company` parameter on the three operations.
- [ ] **Step 6: Sweep.** `JobWorker.RunClaimedAttemptAsync`: when `resolver.MultiCompany` is false, unchanged. Otherwise presence check once (not open → `RecordSkippedAsync` per selected company with `quickbooks-not-open`); then for each `CompanySweep.Select(...)` company in id order, `listSync.SyncWithRunAsync(context, Schedule, null, ct)`; a failure of one company is logged and the sweep continues; the attempt's outcome is Ok when every selected company synced. `Select`: only `CanOpenSession` companies; `waiting-only` = pending > 0 or no attempt ever; `all` = all; `off` = none.
- [ ] **Step 7: Run the tests** (+ `ScheduledListSyncTests`, `V2QbListsSyncTests`, `V2QbListSyncHistoryTests`, `QbListsSourceDbWiringTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 8: Tracker row T-1709 and Session-log line.**
- [ ] **Step 9: Commit**

```bash
git add src/QbAutopost.Core/Abstractions/IQbListsCatalog.cs src/QbAutopost.Core/Pipeline/QbListSync.cs src/QbAutopost.Api/Jobs/CompanySweep.cs src/QbAutopost.Api/Jobs/JobWorker.cs src/QbAutopost.Api/Jobs/ListSyncSchedule.cs src/QbAutopost.Api/Jobs/DirectPostRunner.cs src/QbAutopost.Api/QuickBooks/DbQbListsSource.cs src/QbAutopost.Api/QuickBooks/FileQbListsCatalog.cs src/QbAutopost.Data/EfQbListsStore.cs src/QbAutopost.Api/Endpoints/V2/QuickBooksListsV2Endpoints.cs src/QbAutopost.Api/Endpoints/V2/CompanyEndpoints.cs src/QbAutopost.Api/Endpoints/V2/CompanyParameter.cs src/QbAutopost.Api/Program.cs src/QbAutopost.Api/wwwroot/openapi-v2.json tests/QbAutopost.Data.Tests/QbListSyncStoreTests.cs tests/QbAutopost.Api.Tests/Api/V2CompanyListsTests.cs tests/QbAutopost.Api.Tests/Jobs/ScheduledCompanySweepTests.cs tests/QbAutopost.Api.Tests/Jobs/CompanySweepTests.cs docs/tracker.md
git commit -m "T-1709: QuickBooks lists per company, company parameter, scheduled sweep"
```

---

### Task T-1710: `POST /companies/{id}/check`

**Files:**
- Create: `src/QbAutopost.Core/QbXml/QbCompanyQuery.cs`, `tests/fixtures/qbxml/company-query.golden.xml`, `Companies/CompanyAccessChecker.cs`
- Modify: `Endpoints/V2/CompanyEndpoints.cs`, `Endpoints/ApiRoutes.cs`, `Companies/CompanyProblems.cs`, `QuickBooks/SimulatedQuickBooks.cs` (answers `CompanyQueryRq`), `Program.cs`, `wwwroot/openapi-v2.json`
- Test: `tests/QbAutopost.Core.Tests/QbXml/QbCompanyQueryTests.cs`, `tests/QbAutopost.Api.Tests/Companies/CompanyCheckTests.cs`, `tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs` (+)

**Interfaces:**
- Consumes: `ICompanyStore.RecordAccessAsync`, `CompanyAccessCheck` (T-1703); `For`, `QbTarget`, wrong-company exceptions (T-1706); `JobQueue.RunExclusiveAsync`, `IQbPresence`.
- Produces:

```csharp
public sealed record QbCompanyInfo(QbStatus Status, string? CompanyName, string? LegalCompanyName);
public static class QbCompanyQuery
{
    public static string Build(string qbXmlVersion = QbXmlBuilder.DefaultVersion);   // <CompanyQueryRq requestID="1"/>
    public static QbCompanyInfo Parse(string qbxml);                                 // CompanyRet/CompanyName, LegalCompanyName
}
public sealed class CompanyAccessChecker(IQbGateway gateway, PipelineOptions options, IClock clock)
{
    /// <summary>The one call that may open a session for a company that is not approved (spec §4.7). Throws QuickBooksWrongCompanyException.</summary>
    public Task<CompanyAccessCheck> CheckAsync(Company company, CancellationToken ct);
}
// SimulatedQuickBooks gains: string CompanyName { get; set; } = "Simulated Company"; string? LegalCompanyName { get; set; }
// CompanyProblems gains: QuickBooksNotOpen() → 503 quickbooks-not-open
```

- [ ] **Step 1: Write the failing tests**

```csharp
// QbCompanyQueryTests
[Fact] public void Should_MatchGoldenFile_When_Built()                  // tests/fixtures/qbxml/company-query.golden.xml
[Fact] public void Should_ReadNames_When_CompanyRetPresent()
[Fact] public void Should_BeReadOnly_When_Built()                        // QbXmlRequests.IsReadOnly(Build()) is true

// CompanyCheckTests (real .qbw files under a TempDir root, scanned first; fake.CompanyFor(path).CompanyName = "Takoma Park Grocery Store Inc")
[Fact] public async Task Should_RecordApprovedAndNames_When_SessionOpens()
// admin POST /api/v2/companies/{id}/check → 200 Company {access "approved", qbCompanyName "Takoma Park Grocery Store Inc", accessCheckedUtc set}; audit company-checked
[Fact] public async Task Should_Answer409OpenElsewhereAndKeepAccess_When_AnotherFileIsOpen()   // openFile, neededFile; access unchanged
[Fact] public async Task Should_RecordError_When_SessionCannotOpen()     // fake.QueryThrow = QuickBooksUnavailableException → 200, access "error", accessMessage set
[Fact] public async Task Should_RecordNeedsApproval_When_CallOutlivesTheBusyTimeout()  // QuickBooks:BusyTimeoutSeconds tiny, fake.QueryHang → access "needs-approval"
[Fact] public async Task Should_Answer503NotOpenWithoutASession_When_QuickBooksIsNotRunning()  // FakeQbPresence closed; fake.Requests empty
[Fact] public async Task Should_RecordFileNotFoundWithoutASession_When_QbwIsGone()
[Fact] public async Task Should_CheckAnUnapprovedCompany_When_AdminAsks()          // the one exception to the approved-only rule
[Fact] public async Task Should_Answer404_When_CompanyIsUnknown()
[Theory] viewer/editor 403, admin 200
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test --filter "FullyQualifiedName~QbCompanyQueryTests|FullyQualifiedName~CompanyCheckTests"` — FAIL.
- [ ] **Step 3: `QbCompanyQuery`** via `QbMessageSet.Build(version, "stopOnError", …)` like `QbHostQuery`; golden file written from the first `Build()` output and reviewed by eye. `SimulatedQuickBooks.Process` answers `CompanyQueryRq` with status 0 and `CompanyRet`.
- [ ] **Step 4: `CompanyAccessChecker.CheckAsync`.** `FilePath` null or `!File.Exists` → `FileNotFound`, no session. Else `gateway.For(new QbTarget(company.FilePath)).ProcessAsync(QbCompanyQuery.Build(options.QbXmlVersion), ct)` (the busy timeout applies through `ResilientQbGateway`): status 0 → `Approved` + names; `QuickBooksBusyException` → `NeedsApproval` with "QuickBooks did not answer within the busy timeout; answer the certificate dialog on the server" (conflict 3); `QuickBooksWrongCompanyException` → rethrow; other `QuickBooksUnavailableException`/`QuickBooksCallException`/non-zero status → `Error` with the message (never a path beyond the company file).
- [ ] **Step 5: Route.** `POST /companies/{id}/check`: 404 unknown; `ListSyncGuard.IsOpen(presence, log)` false → `CompanyProblems.QuickBooksNotOpen()`; else `queue.RunExclusiveAsync($"company-check-{id}", t => checker.CheckAsync(company, t), ct)`; wrong-company → `CompanyProblems.OpenElsewhere`; else `RecordAccessAsync(id, check, caller.Actor)` → 200 `CompanyView` + ETag. `V2Routes` += `("POST","/companies/{id}/check")`; `ScopeFor` → `RequestsAdmin`; OpenAPI operation.
- [ ] **Step 6: Run the tests** (+ `OpenApiV2DriftTests`, `V2RoutingTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 7: Tracker row T-1710 and Session-log line.**
- [ ] **Step 8: Commit**

```bash
git add src/QbAutopost.Core/QbXml/QbCompanyQuery.cs tests/fixtures/qbxml/company-query.golden.xml src/QbAutopost.Api/Companies/CompanyAccessChecker.cs src/QbAutopost.Api/Companies/CompanyProblems.cs src/QbAutopost.Api/Endpoints/V2/CompanyEndpoints.cs src/QbAutopost.Api/Endpoints/ApiRoutes.cs src/QbAutopost.Api/QuickBooks/SimulatedQuickBooks.cs src/QbAutopost.Api/Program.cs src/QbAutopost.Api/wwwroot/openapi-v2.json tests/QbAutopost.Core.Tests/QbXml/QbCompanyQueryTests.cs tests/QbAutopost.Api.Tests/Companies/CompanyCheckTests.cs tests/QbAutopost.Api.Tests/Auth/V2ScopeMatrixTests.cs docs/tracker.md
git commit -m "T-1710: Check access route with CompanyQueryRq"
```

---

### Task T-1711: Prompt `2026-11-03.api-2` and the extraction's company match

**Files:**
- Modify: `Extraction/extraction-prompt.json`, `Extraction/StatementExtractionService.cs`, `src/QbAutopost.Core/Extraction/ExtractionAnswer.cs`, `src/QbAutopost.Data/EfSavedRequestStore.Upload.cs` (store/read `company_id`, `company_match` on extractions), `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.Extractions.cs`, `wwwroot/openapi-v2.json`
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/ExtractionPromptTests.cs` (pin), `tests/QbAutopost.Api.Tests/SavedRequests/V2ExtractionCompanyTests.cs`, `tests/QbAutopost.Data.Tests/ExtractionStoreContract.cs` (+1)

**Interfaces:**
- Consumes: `CompanyNameMatcher.Match`, `CompanyMatch` (T-1701); `ICompanyStore.ListAsync`, `Company.ForMatching()` (T-1703); `CompanyRefView` (T-1705).
- Produces:

```csharp
// ExtractionAnswer gains: string? AccountHolderName { get; init; }  string? DbaName { get; init; }   (JSON accountHolderName, dbaName)
public sealed record ExtractionCompanyView(string Match, string? PrintedName, string? DbaName, int? CompanyId, IReadOnlyList<CompanyRefView> Candidates);
// StatementExtractionView gains a last positional member: ExtractionCompanyView Company
// StatementExtraction.Company (T-1703) is set and stored (statement_extractions.company_id = match.CompanyId, company_match jsonb)
```

- [ ] **Step 1: Write the failing tests**

```csharp
// ExtractionPromptTests: PinnedVersion = "2026-11-03.api-2"; PinnedSha256 = the new file's hash (Step 3)
[Fact] public void Should_OfferOptionalPrintedNameFields_When_SchemaLoaded()   // properties accountHolderName, dbaName: type string; not in "required"
[Fact] public void Should_ForbidFileNameForCompanyFields_When_PromptLoaded()   // system prompt states: copy exactly as printed; omit if not printed; never from the file name

// V2ExtractionCompanyTests (ExtractionTestHost; companies 1 Takoma, 2 S & A in FakeCompanyStore; FakeOpenRouterHandler reply fixtures)
[Fact] public async Task Should_MatchTakoma_When_HeaderPrintsTakomaParkGroceryStoreInc()
// reply accountHolderName "TAKOMA PARK GROCERY STORE INC", dbaName "DBA ZEMEN MARKET" → company {match "matched", printedName …, dbaName …, companyId 1, candidates [{id 1, code "18", displayName "Takoma Park Grocery Store Inc"}]}
[Fact] public async Task Should_MatchSAndA_When_HeaderPrintsSaConvenienceMarket()   // "SA CONVENIENCE MARKET" → companyId 2
[Fact] public async Task Should_ReturnNone_When_NoNameIsPrinted()                    // printedName null, companyId null, candidates []
[Fact] public async Task Should_ReturnAmbiguous_When_TwoCompaniesShareTheName()
[Fact] public async Task Should_IgnoreTheFileName_When_HeaderHasNoName()
// upload named "Bank statement 18 ; Takoma park grocery store inc.pdf", reply without names → match "none";
// Handler.Requests' body contains no "Takoma park grocery store inc"
[Fact] public async Task Should_StoreTheMatchOnTheExtraction_When_Extracted()      // Store.GetExtractionAsync(id).Company equals the answer's
[Fact] public async Task Should_KeepPrintedNamesOutOfThePayload_When_Extracted()   // payload has no accountHolderName/dbaName
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tests/QbAutopost.Api.Tests --filter "FullyQualifiedName~ExtractionPromptTests|FullyQualifiedName~V2ExtractionCompanyTests"` — FAIL.
- [ ] **Step 3: Prompt file.** `promptVersion` → `2026-11-03.api-2`; schema `properties` += `"accountHolderName": {"type": "string"}`, `"dbaName": {"type": "string"}` (not required; `additionalProperties` stays false); new rule 10 in `systemPrompt`: "'accountHolderName': the business name printed as the account holder in the statement header, copied exactly as printed; 'dbaName': a 'DBA …' name if printed. Omit either if it is not printed. Never take them from the file name." Compute the new pin with `sha256sum src/QbAutopost.Api/Extraction/extraction-prompt.json` (or `certutil -hashfile … SHA256`) and update `PinnedSha256`.
- [ ] **Step 4: Service.** After `IsBankStatement` and before the record: `var companies = await companyStore.ListAsync(ct)` (empty when no store) → `CompanyNameMatcher.Match(answer.AccountHolderName, answer.DbaName, companies.Select(c => c.ForMatching()))`; set `record.Company`; the view's `Company` maps candidate ids to `CompanyRefView`s. `ExtractionTidy` is unchanged (names never enter the payload). The user message stays `read.Text` only.
- [ ] **Step 5: Stores.** EF and fake extraction stores write and read `company_id` and `company_match`.
- [ ] **Step 6: OpenAPI** `StatementExtraction.company` schema. Run the tests (+ `V2ExtractionTests`, `ExtractionFinalReviewTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 7: Tracker row T-1711 and Session-log line** (new prompt hash).
- [ ] **Step 8: Commit**

```bash
git add src/QbAutopost.Api/Extraction/extraction-prompt.json src/QbAutopost.Api/Extraction/StatementExtractionService.cs src/QbAutopost.Core/Extraction/ExtractionAnswer.cs src/QbAutopost.Data/EfSavedRequestStore.Upload.cs src/QbAutopost.Api/wwwroot/openapi-v2.json tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.Extractions.cs tests/QbAutopost.Api.Tests/SavedRequests/ExtractionPromptTests.cs tests/QbAutopost.Api.Tests/SavedRequests/V2ExtractionCompanyTests.cs tests/QbAutopost.Data.Tests/ExtractionStoreContract.cs docs/tracker.md
git commit -m "T-1711: prompt 2026-11-03.api-2 with printed names; extraction company match"
```

---

### Task T-1712: Company on statements, the *needs company* hold, `set-company`, duplicate scoping

**Files:**
- Modify: `SavedRequests/{SavedRequestIntake,SavedRequestPoster,SavedRequestValidator,SavedRequestViewsV2,V2QueryParser}.cs`, `Endpoints/SavedRequestEndpoints.cs`, `Endpoints/SavedRequestEndpoints.Upload.cs`, `Endpoints/ApiRoutes.cs`, `wwwroot/openapi-v2.json`, `src/QbAutopost.Core/SavedRequests/{DuplicateDetector,SavedRequestRules,SavedRequestModels,SavedRequestV2Models,ISavedRequestStore,ISavedRequestV2Store,SaveMetadata}.cs`, `src/QbAutopost.Core/Extraction/UploadSave.cs`, `src/QbAutopost.Data/EfSavedRequestStore{,.V2,.Upload}.cs`, `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore{,.V2,.Upload}.cs`
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/V2CompanyStatementTests.cs`, `tests/QbAutopost.Core.Tests/SavedRequests/{DuplicateDetectorTests,SavedRequestRulesTests}.cs` (+), `tests/QbAutopost.Data.Tests/{SavedRequestStoreContract,ListV2StoreContract,DashboardV2StoreContract}.cs` (+)

**Interfaces:**
- Consumes: `CompanyContext`, `ICompanyResolver.AutoAssignAsync`, `CompanySource`, `CompanyMatch` (T-1703/4); `CompanyProblems.Required/NotReady` (T-1708); `IQbListsCatalog` (T-1709); `StatementExtraction.Company` (T-1711); `CompanyRefView` (T-1705).
- Produces:

```csharp
// NewSavedRequest gains init: int? CompanyId; CompanySource? CompanySource; CompanyMatch? CompanyMatch
public sealed record CompanyFilter(int? CompanyId)   // CompanyId null = "none"
{
    public static CompanyFilter None { get; } = new((int?)null);
}
// SavedRequestQuery gains init: CompanyFilter? Company;  ISavedRequestV2Store.StatsV2Async(CompanyFilter? company, CancellationToken ct)
// SavedRequestStatsV2 gains init: int NeedsCompany
// ISavedRequestStore:
Task<MutationResult> SetCompanyAsync(long id, int expectedRowVersion, int companyId, DuplicateMatch duplicate, string actor, DateTime nowUtc, CancellationToken ct);
//   NotFound / Stale / WrongStatus (not pending, or an attempt in-progress); Ok clears validation and validation_v2, sets company_source 'chosen',
//   writes saved_request_events kind "company-assigned" detail {from, to, source:"chosen"}
// DuplicateDigest gains a last optional member: int? CompanyId = null
public static DuplicateMatch Detect(string canonicalSha256, string? reference, IReadOnlySet<string> rowFingerprints, IEnumerable<DuplicateDigest> others, int? companyId);
// SavedRequestAction gains SetCompany; SavedRequestRules:
public enum CompanyGate { Ready, Required, NotReady }
public static IReadOnlyList<string> AllowedActionNames(SavedRequestStatus status, IReadOnlySet<string> scopes, bool isUser, CompanyGate company); // v2 only
// SavedRequestIntake:
public Task<MutationResult> SetCompanyAsync(long id, int expectedRowVersion, int companyId, Caller caller, CancellationToken ct);
// v2 views: SavedRequestSummaryV2View/SavedRequestDetailV2View gain CompanyRefView? Company, string? CompanySource; detail gains CompanyMatchView? CompanyMatch
public sealed record CompanyMatchView(string Match, string? PrintedName, string? DbaName, IReadOnlyList<int> Candidates);
// UploadSave reads upload.companyId (int, optional) beside upload.extractionId
```

- [ ] **Step 1: Write the failing tests**

```csharp
// Core
[Fact] public void Should_FlagSamePayload_When_CompaniesDiffer()                   // DuplicateDetectorTests
[Fact] public void Should_NotFlagSameReference_When_CompaniesDiffer()
[Fact] public void Should_NotFlagOverlappingRows_When_EitherStatementHasNoCompany()
[Fact] public void Should_FlagSameReferenceAndRows_When_SameCompany()
[Fact] public void Should_OfferSetCompanyNotValidateOrPost_When_PendingWithoutCompany()   // SavedRequestRulesTests, editor scopes
[Fact] public void Should_OmitValidateAndPost_When_CompanyIsNotReady()
[Fact] public void Should_OfferNoSetCompany_When_NotPending()

// Data contracts (Fake + Ef)
[ContractFact] public async Task Should_RoundTripCompanyAndSource_When_Created()
[ContractFact] public async Task Should_ClearValidationAndRecordEvent_When_CompanySet()
[ContractFact] public async Task Should_ReturnStale_When_SetCompanyRowVersionIsOld()
[ContractFact] public async Task Should_ReturnWrongStatus_When_SettingCompanyWhilePosting()     // Review Focus 3
[ContractFact] public async Task Should_FilterByCompanyAndNone_When_Listing()
[ContractFact] public async Task Should_CountNeedsCompany_When_Stats()
[ContractFact] public async Task Should_CarryCompanyId_When_ReadingDuplicateCandidates()

// V2CompanyStatementTests (two approved companies, RootFolder set; one-company and RootFolder-empty variants where named)
[Fact] public async Task Should_SaveUnassignedAndHold_When_PdfHeaderHasNoRecognisableName()   // A-5
// upload with match none → company null, companySource null; allowedActions contains "set-company", not "validate"/"post";
// POST validate → 409 company-required; POST post → 409 company-required; no attempt
[Fact] public async Task Should_AssignTheMatchedCompany_When_UploadMatched()                 // source "matched"; detail companyMatch {match "matched", printedName "TAKOMA PARK GROCERY STORE INC"}
[Fact] public async Task Should_AssignTheChosenCompany_When_UploadSendsCompanyId()           // upload.companyId 2 → source "chosen"
[Fact] public async Task Should_Answer400_When_UploadCompanyIdIsMissingOrDisabled()          // Review Focus 4; nothing saved
[Fact] public async Task Should_AssignDefaultAndPost_When_RootFolderIsEmpty()                // A-13: n8n save → default, source "default"; post → 200 posted
[Fact] public async Task Should_AssignTheOnlyActiveCompany_When_OneCompanyIsActive()
[Fact] public async Task Should_LeaveEmailedStatementUnassigned_When_TwoCompaniesAreActive()
[Fact] public async Task Should_ClearValidationAndAudit_When_CompanyIsSet()                  // A-6: POST /api/v2/saved-requests/{id}/company {companyId 1} If-Match → 200, validation null, event company-assigned
[Fact] public async Task Should_ValidateWithThatCompanysListsAndRules_When_CompanyIsSet()    // A-6: lists differ (catalog.StoreFor(1/2).RecordAsync) → unknownNames differ between the two companies
[Fact] public async Task Should_Answer409_When_SettingCompanyWhilePosting()
[Fact] public async Task Should_Answer412_When_SetCompanyIfMatchIsStale()
[Theory][InlineData("abc")][InlineData("0")] public async Task Should_Answer400_When_CompanyFilterIsMalformed(string v)
[Fact] public async Task Should_ReturnEmptyPage_When_CompanyFilterIsUnknownId()
[Fact] public async Task Should_FilterNeedsCompany_When_CompanyIsNone()                      // company=none, stats needsCompany
[Fact] public async Task Should_CompareReferencesOnlyWithinACompany_When_DetectingDuplicates()
[Fact] public async Task Should_Answer409CompanyRequired_When_V1PostOfUnassignedStatement() // conflict 8; v1 body shape is a problem detail
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test --filter "FullyQualifiedName~DuplicateDetectorTests|FullyQualifiedName~SavedRequestRulesTests|FullyQualifiedName~V2CompanyStatementTests|FullyQualifiedName~StoreContract"` — FAIL.
- [ ] **Step 3: Core.** `DuplicateDetector.Detect(…, companyId)`: `same-payload` against every other; `same-reference` and `overlapping-rows` only when `companyId is not null && other.CompanyId == companyId`; the old overload is removed and every caller passes the statement's company. `SavedRequestRules`: `Allowed(status, scopes, isUser, CompanyGate)`: `Required` removes Validate/Post and adds SetCompany (pending + `requests:edit`); `NotReady` removes Validate/Post and adds SetCompany; `Ready` adds SetCompany for pending + `requests:edit`. The v1 overload is unchanged and never yields `set-company`. Name `"set-company"` appended after `"resolve"` in the names list.
- [ ] **Step 4: Stores.** `CreateAsync` and `SaveUploadAsync` write `company_id`, `company_source`, `company_match`; `DuplicateCandidatesAsync` returns `CompanyId`; list/stats apply `CompanyFilter`; `SetCompanyAsync` as in Interfaces (one transaction, `row_version + 1`). Fake store mirrors.
- [ ] **Step 5: Intake.** Save (v1/v2 API): `company = await resolver.AutoAssignAsync()` → source `Default`, else null. Upload save: precedence of conflict 13 — `upload.companyId` (must exist and be `Active` or `Ambiguous`, else 400 `invalid-request` before anything is stored) → auto-assign → `extraction.Company` with `Matched` → null; `company_match` copied from the extraction. Validation with no company: `SavedRequestValidator` returns `Unavailable` with `[CompanyRequired]` without planning (`// SPEC-GAP T-1712`, conflict 7); with a company, plans with its context. Duplicates use the statement's company. `SetCompanyAsync`: company must exist and be `Active`/`Ambiguous` (else 400), then detect duplicates with the new company and call the store.
- [ ] **Step 6: Routes.** `POST /saved-requests/{id}/company` (v2 only) body `{ "companyId": n }`: `If-Match` required (428/412 as edit); `WrongStatus` → 409 `not-pending`-style problem as the other changes use; 200 with the v2 detail and new ETag. `SavedRequestScope`: `(3, "POST")` `"company"` → `RequestsEdit`; `V2Routes` += `("POST","/saved-requests/{id}/company")`. Validate and post (v1 and v2): `record.CompanyId is null` → `CompanyProblems.Required(record.PublicRef)` before anything else (replace the T-1708 `// T-1712` fallback in `SavedRequestPoster`). `GET /saved-requests` and `/stats` (v2): `company` parsed by `V2QueryParser` (`none` or a positive integer, single-valued, else 400). v2 views fill `Company`, `CompanySource`, `CompanyMatch` and the company-aware `AllowedActions` (one `ICompanyStore.ListAsync` per response for names and readiness). OpenAPI updated.
- [ ] **Step 7: Run the tests** (+ every `SavedRequests` and `Data` contract test, `V1ContractPinTests`, `V1UnchangedTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 8: Tracker row T-1712 (+ Questions rows for conflicts 7, 12, 13) and Session-log line.**
- [ ] **Step 9: Commit**

```bash
git add src/QbAutopost.Api/SavedRequests/ src/QbAutopost.Api/Endpoints/SavedRequestEndpoints.cs src/QbAutopost.Api/Endpoints/SavedRequestEndpoints.Upload.cs src/QbAutopost.Api/Endpoints/ApiRoutes.cs src/QbAutopost.Api/wwwroot/openapi-v2.json src/QbAutopost.Core/SavedRequests/ src/QbAutopost.Core/Extraction/UploadSave.cs src/QbAutopost.Data/EfSavedRequestStore.cs src/QbAutopost.Data/EfSavedRequestStore.V2.cs src/QbAutopost.Data/EfSavedRequestStore.Upload.cs tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.cs tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.V2.cs tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.Upload.cs tests/QbAutopost.Api.Tests/SavedRequests/V2CompanyStatementTests.cs tests/QbAutopost.Core.Tests/SavedRequests/DuplicateDetectorTests.cs tests/QbAutopost.Core.Tests/SavedRequests/SavedRequestRulesTests.cs tests/QbAutopost.Data.Tests/SavedRequestStoreContract.cs tests/QbAutopost.Data.Tests/ListV2StoreContract.cs tests/QbAutopost.Data.Tests/DashboardV2StoreContract.cs docs/tracker.md
git commit -m "T-1712: company on statements, needs-company hold, set-company, duplicate scoping"
```

---

### Task T-1713: `company` filter on `/dashboard/summary` and `/audit-events`; `companyId` on events

**Files:**
- Modify: `Endpoints/V2/{DashboardSummaryEndpoints,AuditEventEndpoints}.cs`, `src/QbAutopost.Core/Dashboard/IDashboardSummaryStore.cs`, `src/QbAutopost.Core/SavedRequests/Audit/{AuditQuery,AuditEvent}.cs`, `src/QbAutopost.Data/EfSavedRequestStore{.Summary,.Audit}.cs`, `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore{.Summary,.Audit}.cs`, `wwwroot/openapi-v2.json`
- Test: `tests/QbAutopost.Api.Tests/SavedRequests/V2CompanyFilterTests.cs`, `tests/QbAutopost.Data.Tests/{AuditEventsStoreContract,SummaryStoreContract}.cs` (+)

**Interfaces:**
- Consumes: `audit_events.company_id` (T-1703); `AuditEventRow.CompanyId`; statements with companies (T-1712); `CompanyParameter`-style parsing (T-1709).
- Produces:

```csharp
// AuditQuery gains init: int? CompanyId;  AuditEvent gains init: int? CompanyId;  AuditEventView gains int? CompanyId (v2 JSON "companyId")
// IDashboardSummaryStore: EventFactsAsync/PostedFactsAsync/PostTimeFactsAsync gain `int? companyId` before ct (null = all companies)
```

- [ ] **Step 1: Write the failing tests**

```csharp
[ContractFact] public async Task Should_CarryCompanyId_When_EventBelongsToACompanysStatement()      // AuditEventsStoreContract
[ContractFact] public async Task Should_ReturnOnlyThatCompanysEvents_When_FilteredByCompany()
[ContractFact] public async Task Should_IncludeCompanyEvents_When_CallerLacksUsersManage()          // companies-scanned visible (id prefix c)
[ContractFact] public async Task Should_CountOnlyThatCompany_When_SummaryFilteredByCompany()      // SummaryStoreContract
[Fact] public async Task Should_FilterSummaryByCompany_When_CompanyParameterSent()                 // V2CompanyFilterTests
[Fact] public async Task Should_FilterAuditFeedByCompany_When_CompanyParameterSent()
[Fact] public async Task Should_AnswerAsBefore_When_NoCompanyParameter()                           // same body as before for both routes (all companies)
[Theory][InlineData("none")][InlineData("x")] public async Task Should_Answer400_When_CompanyParameterIsNotAnId(string v)
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test --filter "FullyQualifiedName~V2CompanyFilterTests|FullyQualifiedName~AuditEventsStoreContract|FullyQualifiedName~SummaryStoreContract"` — FAIL.
- [ ] **Step 3: Implement.** `company` added to the summary's and the feed's single-valued parameters (a positive integer; absent = all companies, spec §6 f); the EF queries filter `audit_events.company_id` and `saved_requests.company_id`; the feed and CSV pass the filter; the JSON event view gains `companyId`; the CSV columns are unchanged. Fake stores mirror.
- [ ] **Step 4: Run the tests** (+ `V2AuditEventsTests`, `V2SummaryTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 5: Tracker row T-1713 and Session-log line.**
- [ ] **Step 6: Commit**

```bash
git add src/QbAutopost.Api/Endpoints/V2/DashboardSummaryEndpoints.cs src/QbAutopost.Api/Endpoints/V2/AuditEventEndpoints.cs src/QbAutopost.Core/Dashboard/IDashboardSummaryStore.cs src/QbAutopost.Core/SavedRequests/Audit/AuditQuery.cs src/QbAutopost.Core/SavedRequests/Audit/AuditEvent.cs src/QbAutopost.Data/EfSavedRequestStore.Summary.cs src/QbAutopost.Data/EfSavedRequestStore.Audit.cs tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.Summary.cs tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.Audit.cs src/QbAutopost.Api/wwwroot/openapi-v2.json tests/QbAutopost.Api.Tests/SavedRequests/V2CompanyFilterTests.cs tests/QbAutopost.Data.Tests/AuditEventsStoreContract.cs tests/QbAutopost.Data.Tests/SummaryStoreContract.cs docs/tracker.md
git commit -m "T-1713: company filter on summary and audit feed; companyId on events"
```

---

### Task T-1714: Readiness, start-up log, runbook "Adding a company file"

**Files:**
- Modify: `Health/Readiness.cs`, `Health/AppHealth.cs`, `Logging/StartupLog.cs`, `Program.cs`, `docs/runbook.md` (§2.5 rewritten, §8.3 dialog note)
- Test: `tests/QbAutopost.Api.Tests/HealthChecks/ReadinessChecksTests.cs` (+), `tests/QbAutopost.Api.Tests/Logging/StartupLogTests.cs` (+)

**Interfaces:**
- Consumes: `CompaniesSettings` (T-1704); `ICompanyStore.ListAsync`, `Company.CanOpenSession` (T-1703).
- Produces:

```csharp
public sealed record CompanySummary(bool RootConfigured, bool RootReachable, int Count, int NotApproved);
public static ReadinessView Evaluate(AppSettings settings, bool workerStarted, IQbListsSource lists, DatabaseGate? database = null, CompanySummary? companies = null);
public static void WriteCompanies(ILogger log, CompaniesSettings settings, CompanySummary? summary);   // StartupLog
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Should_AddNoCompaniesCheck_When_RootFolderIsEmpty()                 // readiness unchanged for single-company installs
[Fact] public void Should_WarnWithCounts_When_SomeCompaniesAreNotApproved()           // check "companies", severity warning, message "root …: 3 companies, 1 not approved", ok stays true
[Fact] public void Should_Warn_When_RootFolderIsUnreachable()                          // warning naming the path, ok true
[Fact] public void Should_LogRootCountAndUnapproved_When_Starting()                    // StartupLogTests: "Companies: root {Root} (found), 2 companies, 1 not approved"
[Fact] public void Should_LogMultiCompanyOff_When_RootFolderSetWithoutDatabase()        // Warning "Companies:RootFolder is ignored without a database"
```

- [ ] **Step 2: Run to verify they fail.** `dotnet test tests/QbAutopost.Api.Tests --filter "FullyQualifiedName~ReadinessChecksTests|FullyQualifiedName~StartupLogTests"` — FAIL.
- [ ] **Step 3: Implement.** `Readiness.Evaluate` adds a `companies` check (severity `warning`, never blocks) only when `companies` is non-null and `RootConfigured`. `AppHealth` builds `CompanySummary` from `ICompanyStore` when registered (a store error → no check, logged). `Program.cs` calls `StartupLog.WriteCompanies` after `CompanyBootstrap.RunAsync`.
- [ ] **Step 4: Runbook §2.5 "Adding a company file".** Steps: put each company's `.qbw` in its own subfolder of `Companies:RootFolder` (`<code>_<name>`), set `Companies:RootFolder` (absolute) and restart; open the Companies page → Sync (`POST /api/v2/companies/scan`); for each new company the QuickBooks admin opens that file in QuickBooks, accepts the *QbAutopost* certificate with **"Yes, always; allow access even if QuickBooks is not running"** and picks the QuickBooks user to log in as (N files = N approvals); then **Check access** (`POST /api/v2/companies/{id}/check`) until `approved`; add aliases (DBA names) under match names; a folder with two `.qbw` is `ambiguous` until a file is chosen; `company-file-open-elsewhere` means close the other company in QuickBooks yourself — the app never switches it; a Check access that ends `needs-approval` may leave a certificate dialog open on the server: answer or close it, QuickBooks calls wait until then (conflict 3); the default company keeps `Company:FilePath`, `Company:RulesFile`, `Paths:Ledger`. Use only `/api/v2/...` route spellings.
- [ ] **Step 5: Run the tests** (+ `OperatorDocsTests`) — PASS; `dotnet build -warnaserror`; `dotnet test` — all green.
- [ ] **Step 6: Tracker row T-1714 and Session-log line.**
- [ ] **Step 7: Commit**

```bash
git add src/QbAutopost.Api/Health/Readiness.cs src/QbAutopost.Api/Health/AppHealth.cs src/QbAutopost.Api/Logging/StartupLog.cs src/QbAutopost.Api/Program.cs docs/runbook.md tests/QbAutopost.Api.Tests/HealthChecks/ReadinessChecksTests.cs tests/QbAutopost.Api.Tests/Logging/StartupLogTests.cs docs/tracker.md
git commit -m "T-1714: multi-company readiness, startup line and runbook section"
```

---

### Task T-1715 [server]: End-to-end on the server with two approved companies (A-11)

**Files:**
- Create: `docs/testing/T-1715.server-checklist.md`

**Interfaces:**
- Consumes: everything above; the T-1707 findings.
- Produces: the filled checklist; tracker row `ready-for-human`.

- [ ] **Step 1: Write the checklist** (T-1603 style intro; recent backup; never write a password or key). Items:
  1. **Settings.** `Companies:RootFolder` = `D:\Accountexx Data\Quickbook - Accounting File\Quickbook - Accounting File`; restart; startup line `Companies: root … (found), N companies, M not approved`.
  2. **Scan.** Companies page → Sync: `18_Takoma Park Grocery Store Inc` (code 18) and `440_S & A Convenience Market LLC` (code 440) are `active`; a second Sync shows `added 0`.
  3. **Approve both** (runbook §2.5), Check access each → `approved`, QuickBooks names shown.
  4. **Match.** Upload `Bank statement 18 ; Takoma park grocery store inc.pdf` → company Takoma, printed `TAKOMA PARK GROCERY STORE INC`; upload `Credit card 440 ; S & A CONVENIENCE MARKET LLC.pdf` → S & A, printed `SA CONVENIENCE MARKET`. (If Takoma shows `none`, add alias `Zemen Market` and retry.)
  5. **Post with QuickBooks closed.** Close QuickBooks; post the Takoma statement → posted into Takoma's file (check in QuickBooks afterwards); record the time.
  6. **Wrong file open.** Open S & A in QuickBooks; post another Takoma statement → refused `company-file-open-elsewhere` naming the open file (or `company-file-unavailable` per T-1707), nothing in either file, statement still pending; no retry in the log.
  7. **Lists per company.** Sync lists for S & A; Takoma's lists and history unchanged.
  8. **Scheduled sweep** (`waiting-only`): after one interval, only companies with waiting statements or never synced have new rows; an unapproved company has none.
  9. **v1 unchanged.** `scripts/qb-server-check.ps1` (as shipped) passes against the default company.
- [ ] **Step 2: Verify docs.** `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~OperatorDocsTests"` — PASS.
- [ ] **Step 3: Tracker row T-1715 → `ready-for-human`, M17 milestone row "agent work done", Session-log line; stop here.**
- [ ] **Step 4: Commit**

```bash
git add docs/testing/T-1715.server-checklist.md docs/tracker.md
git commit -m "T-1715: server checklist for two approved companies end to end"
```

---

## Self-review notes

- **Spec coverage:** §4.1 → T-1704; §4.2 → T-1702, T-1705; §4.3 → T-1703; §4.4 → T-1703, T-1704; §4.5 → T-1701, T-1711; §4.6 → T-1712; §4.7 gateway → T-1706, context → T-1708, readiness rule → T-1708/T-1709/T-1710, wrong company → T-1706/T-1708, lists and sweep → T-1709; §5.1 → T-1705, T-1710; §5.2 → T-1712; §5.3 → T-1711, T-1709, T-1713; A-1…A-3 → T-1702/T-1705; A-4 → T-1701; A-5, A-6, A-13 → T-1712; A-7 → T-1708; A-8 → T-1709; A-9 → T-1704; A-10 → `V1ContractPinTests` in every API task; A-11 → T-1715; A-12 → T-1708 + T-1709.
- **Type names used across tasks:** `CompanyNames`, `CompanyMatch`, `CompanyMatchKind` (T-1701); `CompanyState`, `ScanPlan`, `KnownCompanyFolder`, `FolderChange` (T-1702); `Company`, `CompanyAccess`, `CompanySource`, `CompanyContext`, `ICompanyStore`, `ICompanyResolver`, `CompanyAccessCheck` (T-1703); `CompaniesSettings`, `CompanyDataFolder`, `CompanyResolver`, `CompanyBootstrap` (T-1704); `CompanyView`, `CompanyRefView`, `CompanyProblems` (T-1705); `QbTarget`, `For`, `QuickBooksWrongCompanyException`, `QuickBooksCompanyFileUnavailableException`, `QbSessionErrors` (T-1706); `CompanyFileRefusal` (T-1708); `IQbListsCatalog`, `CompanySweep` (T-1709); `QbCompanyQuery`, `CompanyAccessChecker` (T-1710); `ExtractionCompanyView` (T-1711); `CompanyFilter`, `CompanyGate` (T-1712); `CompanySummary` (T-1714).
