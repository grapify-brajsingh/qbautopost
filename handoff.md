# Session Handoff — QbAutopost

Written: 2026-09-16 (session 3) · M0, M1, M2 done · Next step: **M3** (XLSX, PDF text, T2 rows, G1 extended)

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-22) and apply any
that change behaviour first. Then continue with the first todo task in M3 (T-301).
One task at a time, tests green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2 and 3 commits (T-101…T-205) are **local only** — push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 331 passing (Core 263, Api 68); 6 consecutive green runs on Windows. **Not yet run on Linux.** |
| Milestones | M0, M1, M2 done; M3–M8 todo |
| Manual check | Development host, **no Hermes running**: `/health/hermes` → 503 (connection refused); sample job → `failed` "Hermes Spec call failed" (Q-22). With Hermes up, expect `ready` 8 post / 2 held / 1 skipped, total 4942.64 (unchanged mapping) |

## 4. What M2 added (code map delta)

```
src/QbAutopost.Core/
  Abstractions/IHermesClient.cs  HermesRequest(task, systemPrompt, userContent, auditDir, schemaHint), PingAsync/HermesPing,
                                 HermesException ← HermesValidationException (two invalid answers) | HermesUnavailableException (transport)
  Hermes/        HermesClient (retry-with-errors, per-call timeout, audit), HermesOptions, HermesAudit (<task>-<n>.request/response.json),
                 JsonReply (fence strip + parse + Validate), PromptTemplate ({{name}}), PromptLibrary (loads Hermes/prompts/*.md at startup),
                 SpecAnswer (T1 DTO → JobSpec), prompts/spec.md (copied to <app>/Hermes/prompts)
  Pipeline/      HermesSpecReader (T1 → regex-fallback on HermesValidationException); SpecReadResult.Note → spec.json.note
src/QbAutopost.Api/
  Program.cs     HermesOptions, typed HttpClient (infinite HttpClient timeout, infinite handler lifetime), PromptLibrary, HermesSpecReader
  Endpoints/HealthEndpoints.cs   GET /health/hermes ({ ok, model, latencyMs, message }; 503 when not ok)
tests/  Core.Tests/TestSupport/StubHttpHandler (in-memory HTTP), Core.Tests/Hermes/*, Pipeline/HermesSpecReaderTests,
        Api.Tests/Api/{SpecGateApiTests,HealthApiTests}, fixtures/output/sample-spec.golden.json (reviewed by hand)
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-22 all carry conservative choices. New this session: Q-21 (transport failures are not retried; empty key → no `Authorization` header), Q-22 (Hermes unreachable → the job fails; no regex fallback).
2. **Using Hermes for a new task (T2–T4):** add `Hermes/prompts/<task>.md`, a `record … : IValidatable` answer DTO, then call `IHermesClient.CompleteJsonAsync<T>(new HermesRequest(task, prompts.Render(task, values), content, Path.Combine(input.OutputDir, "hermes")), ct)`. Add the task to the `PromptLibrary.Load(..., required)` list in `Program.cs` once its prompt exists. T2–T4 **hold** on `HermesException` (spec §9); only T1 falls back.
3. **FakeHermesClient** (Api tests) serves `tests/fixtures/hermes/<task>.json`, runs `Validate()`, and takes `Respond(req => json | null | throw)`; `Ping` sets the health answer. Core tests use `StubHttpHandler` with the real `HermesClient` (`ReplyContent`, `ReplyRaw`, `Hang`, `Throw`).
4. **Api tests script T1 answers.** Any Api test that writes its own `requirement.txt` must also `_factory.Hermes.Respond(...)`, otherwise the fixture spec (4521/7788) is used. The test host points Hermes at `http://hermes.invalid:8642`.
5. **M3 hints from the spec:** FR-3 says a date that fails the layout format falls back to invariant `DateOnly.TryParse`, but the T-002 choice (Q-1) was "row error, no fallback" — decide in T-301 and record it. FR-4's T2 formula `opening + Σcredits − Σdebits = closing` fits bank statements; for card statements the sign is reversed (charges raise the balance, as in `ReconcileGate`) — treat it as a SPEC-GAP. T2 chunking threshold is 60 000 characters (§9.2). XLSX/PDF statements are currently held `extractor-not-available` in `JobPipeline.ReadStatement`.
6. **Already true from earlier sessions:** JSON enums are camelCase; read shared JSON with `AtomicFile.ReadAllText`; `ApiFactory.WithSetting(key, value)`; GateGuard hook blocks the first write of every file (retry works; `ECC_GATEGUARD=off` disables); console logs lack the `jobId` scope until T-702; sample output and `src/QbAutopost.Api/data/` are git-ignored.
7. **Running by hand:** `dotnet run` needs `ASPNETCORE_ENVIRONMENT=Development` (or a real `QBAUTOPOST__Api__ApiKey`), otherwise startup stops on the `change-me` key (Q-19).

## 6. Next milestone — M3 (XLSX, PDF text, T2, G1 extended)

| Task | Notes |
|---|---|
| T-301 XlsxStatementParser | ClosedXML (approved package) → string grid → `StatementGridParser` (shared with CSV). First worksheet only. |
| T-302 PdfText | PdfPig per page; < 40 chars on a page → scanned → `Ocr` when `Ocr.Enabled`, else held `scanned-pdf-ocr-disabled`. Tesseract optional. |
| T-303 StatementLlmExtractor (T2) | Prompt `statement.md`, schema §9.2, amounts > 0, ISO dates, chunk > 60 000 chars and merge by row order; hold on `HermesException`. |
| T-304 G1 extended + rows.json | opening/closing/count/period checks; `rows.json` `layout` = `"hermes-t2"` for PDF; `not-verifiable` → `reconcile.verified = false`. |
| T-305 Tests | fixture PDF text → expected rows; chunk merge; every G1 failure mode; XLSX layout detection. |

Open follow-ups: run `dotnet test` on Linux (a CI workflow would do); decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy; push sessions 2–3.
