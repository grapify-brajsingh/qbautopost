# CLAUDE.md — QbAutopost.Api

Single .NET 8 app that turns a job folder (requirement text + bank/card statements + invoices) into posted QuickBooks Desktop transactions, using Hermes AI (OpenAI-compatible API at `http://127.0.0.1:8642`) for reading and judgment and deterministic code for money.

## Read first
- `docs/spec.md` — the contract. FR-x sections are acceptance criteria. Spec wins over code.
- `docs/plan.md` — milestones and task order.
- `docs/tracker.md` — what is done; pick the first `todo` in the lowest unfinished milestone.

## Commands
```
dotnet build -warnaserror
dotnet test
dotnet run --project src/QbAutopost.Api
```
Test fixtures live in `tests/fixtures/`; the sample job is `samples/jobs/2026-08-tropicana`.

## Hard rules
1. **No test may contact a real QuickBooks or Hermes.** Use `FakeQbGateway` and `FakeHermesClient`. `dotnet test` must pass on Linux.
2. **COM only in `src/QbAutopost.QuickBooks`.** Nothing in `QbAutopost.Core` may reference `System.Runtime.InteropServices` COM types or `Type.GetTypeFromProgID`. Guard Windows-only code with `[SupportedOSPlatform("windows")]` and `OperatingSystem.IsWindows()`.
3. **The model never computes money.** Amounts come from parsed files or from T2 rows that passed the reconcile gate (G1). Never "fix" a total in code to make a gate pass.
4. **Hold, don't guess.** If the spec is silent, choose the behaviour that posts less (hold the line, fail the job), add a row to `docs/tracker.md › Questions`, mark the code `// SPEC-GAP T-xxx`.
5. **qbXML element order is fixed** (spec FR-9). Change it only with a golden-file test update and a note in the tracker.
6. **Never write secrets** (API keys, provider keys) to logs, `output/`, or fixtures.
7. **Every state transition is persisted** to `output/status.json` before the next step starts.

## Working style
- One task at a time: tests first for Core logic → implement → build + test green → update tracker row (Status, Files, Verified by) → append a Session-log line → commit `T-xxx: <summary>`.
- Keep the POC in `poc/` read-only; port from it, don't edit it.
- Prefer small files with one responsibility; file-scoped namespaces; `record`s for DTOs; `System.Text.Json` with `JsonOptions.Default` (case-insensitive, enums as strings, trailing commas allowed).
- Log with `ILogger<T>`; one correlation id per job (`jobId`) on every line.
- When a task is tagged **[server]**, implement against fakes, set the tracker row to `ready-for-human`, and stop — a person verifies on the QuickBooks server.

## Conventions
- Money: `decimal`, formatted `0.00` invariant. Dates: `DateOnly`, ISO `yyyy-MM-dd`.
- Fingerprint: `sha256(kind|last4|date|amount|direction|checkNo|Normalize(description))`, lower-hex; `RequestId = fingerprint[..16]`.
- Job status enum: `queued analysing ready posting posted partial failed` (+ `undone` on batches).
- Errors from endpoints: RFC 7807 problem details.
- Tests: xUnit, `Should_<behaviour>_When_<condition>` names, one behaviour per test, fixtures over inline blobs.

## Don't
- Don't add NuGet packages beyond: PdfPig, ClosedXML, Serilog.AspNetCore, Serilog.Sinks.File, Tesseract (optional), Microsoft.AspNetCore.Mvc.Testing, Scalar.AspNetCore (approved 2026-09-23 for T-913, Q-46). Ask via the tracker first — and in an unattended run "ask" means **stop the task and record the question**, never add it anyway.
- Don't introduce a database, a message broker, or background timers beyond the single `JobWorker`.
- Don't call Hermes for CSV/XLSX statements; they are parsed by code.
