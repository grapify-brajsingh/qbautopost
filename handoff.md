# Session Handoff — QbAutopost

Written: 2026-09-17 (session 5) · M0–M4 done · Next step: **M5** (tiers 3–4 with Hermes T4, gate G3)

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-30) and apply any
that change behaviour first. Then continue with the first todo task in M5 (T-501).
One task at a time, tests green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2–5 commits (T-101…T-404) are **local only** — push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 554 passing (Core 476, Api 78) on Windows, 3 consecutive green runs. **Not yet run on Linux.** |
| Milestones | M0–M4 done; M5–M8 todo |
| Packages | none added in M4 |
| Manual check | None this session (no Hermes on the dev box, so a real job still fails at T1, Q-22) |

## 4. What M4 added (code map delta)

```
src/QbAutopost.Core/
  Hermes/InvoiceAnswer.cs, Hermes/prompts/invoice.md   T3 DTO + validation (party, role, ISO date, total > 0), prompt
  Extract/InvoiceExtractor.cs     InvoiceReadResult; pdf → PdfText, png/jpg → IOcr (off → unreadable); no chunking (> 60 000 chars → unreadable); HermesException → hermes-failed
  Mapping/InvoiceMatcher.cs       InvoiceMatch; FR-5 amount ±0.005 / date ±InvoiceMatchDays → direction → Fuzzy(description, party) ≥ threshold; ties / shared line → ambiguous
  Mapping/Mapper.cs               optional invoices arg: InvoiceRef = invoice file; payee from a fitting invoice when the description names none (known names only)
  Pipeline/AnalysisResult.cs      InvoiceSummary, AnalysisResult.Invoices
  Pipeline/JobPipeline.cs         takes InvoiceExtractor; invoices read after G2, before mapping; output/invoices/<file>.json
  Output/OutputDocuments.cs       result.json unmatchedInvoices = InvoiceSummary[] (unreadable invoices included)
  Models/HoldReasons.cs           unreadable, no-matching-line, no-invoice-date
src/QbAutopost.Api/
  Program.cs                      invoice.md required at startup; InvoiceExtractor registered
  Endpoints/JobView.cs            + unmatchedInvoices (addition to spec §6)
tests/  TestSupport/TestInvoiceExtractor.cs; Mapping/{InvoiceMatcherTests,MapperInvoiceTests}.cs; Extract/InvoiceExtractorTests.cs;
        Hermes/InvoiceAnswerTests.cs; Api/InvoiceApiTests.cs
        fixtures/invoices/home-depot-88213.pdf(.txt); samples/…/invoices/home-depot-88213.pdf (replaces the .txt stand-in)
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-30 all carry conservative choices. New this session: Q-28 (invoice hold codes, no chunking, "Our company" in the T3 message), Q-29 (matching details: no date, no preferred direction, similarity = description vs party, shared line, where unreadable invoices are reported), Q-30 (`InvoiceRef` = file name; invoice payee only when known and the role fits). **Q-27 now also covers invoices** (posting re-runs T3).
2. **Behaviour changes this session:** the sample job makes a T3 call (tests that count Hermes calls filter by task); the sample invoice is a PDF and matches the Home Depot line; `result.json.unmatchedInvoices` is a list of objects, not strings; the analysis golden has `invoiceRef` on the Home Depot line. Line decisions on the sample are unchanged (8/2/1).
3. **M5 hints:**
   - T4 (`HermesTask.Account`, spec §9.4): add `Hermes/prompts/account.md` and an `AccountAnswer : IValidatable`. Its validation needs the allowed `accounts[]` (case-sensitive exact), but `IValidatable.Validate()` takes no arguments: either carry the list inside the answer after deserialising, or validate in the caller and treat a violation like a failed answer (the client's retry only covers `Validate()`). `tests/fixtures/hermes/account.json` exists.
   - `Mapper` is synchronous; T4 is async. Lines needing tiers 3–4 are currently held `no-account-rule` (`HoldReasons.NoAccountRule`, `TODO(T-502)` in `Mapper.WithVendorAndTiers`). A clean option: keep `Mapper` sync, then run an async tier-3/4 pass in the pipeline over lines held `no-account-rule`.
   - Tier 3 input is ready: `Mapper` holds the matched invoices by request id; `InvoiceFacts.CategoryHint` is the hint; `MappedTxn.InvoiceRef` names the file.
   - `QbLists.Accounts` have an optional `Type` for the §9.4 filter; `Rules.ModelConfidenceThreshold` (0.8) exists; `Confidence.Invoice` / `Confidence.Model` exist in `Models/Enums.cs`.
   - G3 (FR-7): `Model` posts only with confidence ≥ threshold **and** a prior posting of the payee to the same account (ledger); held lines carry top-3 account `candidates`.
4. **Test helpers:** `ScriptedHermes` (Core) validates like the real client; `TestStatementReader.Create(ocr, hermes)` and `TestInvoiceExtractor.Create(ocr, hermes)` build the pipeline's readers; `JobPipelineTests` scripts T2 via `_statementAnswer` / `_cardAnswer` and T3 via `_invoiceAnswer`; in Api tests use `FakeHermesClient.Respond(r => r.Task == … ? json : null)`.
5. **Already true from earlier sessions:** JSON enums are camelCase; read shared JSON with `AtomicFile.ReadAllText`; `ApiFactory.WithSetting(key, value)` (not chainable — use `WithWebHostBuilder` for several keys); console logs lack the `jobId` scope until T-702; sample output and `src/QbAutopost.Api/data/` are git-ignored; `.gitattributes` marks `*.pdf *.xlsx *.png *.jpg` binary; PDF fixtures are rendered from their `.pdf.txt` by a throw-away PdfPig program (one text line per PDF line, Helvetica 10 pt).
6. **Running by hand:** `dotnet run` needs `ASPNETCORE_ENVIRONMENT=Development` (or a real `QBAUTOPOST__Api__ApiKey`). With `Ocr:Enabled=true`, set `Ocr:TessDataPath` to a folder holding `eng.traineddata`.
7. **GateGuard hook** blocks the first edit/creation of every file until facts are stated (retry works); `ECC_GATEGUARD=off` disables it.

## 6. Next milestone — M5 (tiers 3–4, G3)

See `docs/plan.md` T-501…T-504 and spec FR-6 (tiers), FR-7 (G3), §9.4 (T4 schema and validation), §10 (`analysis.json` tier, confidence, candidates, decision).

Open follow-ups: run `dotnet test` on Linux (a CI workflow would do); decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy; push sessions 2–5; answer Q-27 before M6.
