# ADR-0013: Multiple company files

**Date**: 2026-11-04
**Status**: accepted
**Deciders**: Braj (owner), Claude Code

## Context

Q-44 (T-804) and Q-50 (T-904) fixed the app to one company file per installation: one `Company` setting, one rules
file, one ledger, and no way for a caller to name a file. The business now keeps many QuickBooks Desktop company files
on one server. QuickBooks Desktop has one company file open at a time, and each file needs a one-time certificate
approval ("allow this application to access this company file") before the SDK may open it. A person may be using
QuickBooks while the app runs, so the app must never close or switch the file they have open. Spec:
`docs/multi-company-m17.md`.

## Decision

One installation serves many company files (spec §1 goals):

1. Companies are found by a one-level scan of `Companies:RootFolder` and stored in a new `companies` table; the scan
   is re-run from the dashboard.
2. Each uploaded PDF is matched to a company from the business name printed in the PDF (never the file name). The
   model only reads the name; code decides, by exact equality after normalising, with no fuzzy distance. One confident
   match assigns the company; anything else holds the statement as *needs company* until a person picks one.
3. A `CompanyContext` (file, rules, ledger, lists) is passed to validation, list sync, duplicate checks and posting.
4. One `ResilientQbGateway` and one lock serve all companies; the company file is a per-call target. If another file
   is open the post is refused (`company-file-open-elsewhere`) and never retried.
5. The default company keeps the original files (`Company:FilePath`, `Company:RulesFile`, `Paths:Ledger`); nothing is
   copied. v1 routes (`/jobs`, `JobPipeline`, `/quickbooks/*`) use it and stay byte-identical.

Only `active` companies with access `approved` open a session, except the admin's Check access. One EF migration
(`MultiCompany`) adds the table and nullable `company_id` columns.

## Alternatives Considered

### Alternative 1: One installation per company
- **Pros**: No code change; strong isolation.
- **Cons**: A second API process is blocked by `start-all.ps1`; N ports, ledgers and tasks; the dashboard sees one company.
- **Why not**: Does not scale to many files and the dashboard needs one view.

### Alternative 2: Fuzzy matching of the printed name
- **Pros**: Fewer statements held.
- **Cons**: A wrong guess posts money to the wrong company.
- **Why not**: Hold, don't guess. Aliases (`matchNames`) cover the rest.

### Alternative 3: Switch the open QuickBooks file ourselves
- **Pros**: Fewer refusals.
- **Cons**: Interferes with a person using QuickBooks.
- **Why not**: Anti-goal; the app refuses and names the open and needed files.

## Consequences

### Positive
- One dashboard and one API for all companies; statements, lists, rules and duplicates are scoped per company.
- A wrong-file post is refused before anything is sent.
- A single-company install and every v1 caller behave as before.

### Negative
- Each company file needs its certificate approval once on the server (N approvals).
- A post for company B waits or is refused while someone has company A open.
- Check access cannot cancel a pending approval dialog; the gateway lock stays held until it is answered (Q-146).

### Risks
- The SDK error for "a different file is open" is not yet confirmed; until T-1707 it is mapped conservatively and never retried (Q-143, ruling B1).
- The default company bypasses `Companies:RootFolder` by design; it must stay the configured `Company:FilePath` only.
