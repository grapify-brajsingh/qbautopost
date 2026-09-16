# Session Handoff — QbAutopost

Written: 2026-09-16 (session 4) · M0–M3 done · Next step: **M4** (invoices: Hermes T3 + matcher)

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-27) and apply any
that change behaviour first. Then continue with the first todo task in M4 (T-401).
One task at a time, tests green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2–4 commits (T-101…T-305) are **local only** — push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 455 passing (Core 382, Api 73) on Windows. **Not yet run on Linux.** |
| Milestones | M0–M3 done; M4–M8 todo |
| Packages added in M3 | ClosedXML 0.105.1 and PdfPig 0.1.16 (Core), Tesseract 5.2.0 (Api) — all on the CLAUDE.md list |
| Manual check | Real Tesseract OCR verified once on Windows (scratch program, `eng.traineddata` not committed). No Hermes on the dev box, so a real job still fails at T1 (Q-22) |

## 4. What M3 added (code map delta)

```
src/QbAutopost.Core/
  Abstractions/IOcr.cs            IOcr (Enabled, ReadImageAsync) + DisabledOcr
  Extract/XlsxGrid.cs             first worksheet → string grid (culture-free; dates → yyyy-MM-dd; formulas → saved result)
  Extract/XlsxStatementParser.cs  grid → StatementGridParser(acceptIsoDates: true); unopenable → unreadable-statement
  Extract/PdfText.cs              PdfPig per page; < 40 non-space chars = scanned → OCR or scanned-pdf-ocr-disabled
  Extract/StatementLlmExtractor.cs  Hermes T2: 60 000-char page-group chunks, merge, hermes-failed / extraction-conflict
  Extract/StatementParseResult.cs   + Totals (StatementTotals: period, opening, closing, count)
  Hermes/StatementAnswer.cs, Hermes/prompts/statement.md   T2 DTO + validation, prompt
  Gates/ReconcileGate.cs          + CheckExtraction (T2 G1: totals, count, period, partial running balances; card sign)
  Pipeline/StatementReader.cs     csv / xlsx / pdf(→PdfText→T2) — JobPipeline now takes this instead of IOcr
src/QbAutopost.Api/
  Ocr/TesseractOcr.cs             used only when Ocr:Enabled; missing eng.traineddata stops startup
  Program.cs                      IOcr, StatementLlmExtractor, StatementReader; statement.md required at startup
tests/  TestSupport/{XlsxBuilder,PdfBuilder,TestImage,FakeOcr,ScriptedHermes,TestStatementReader}.cs
        fixtures/statements/{chase-checking-4521.xlsx, chase-checking-4521.pdf(.txt), chase-card-7788.pdf(.txt)}
        fixtures/hermes/statement-card-7788.json
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-27 all carry conservative choices. New this session: Q-23 (dates: Q-1 kept, XLSX also accepts ISO), Q-24 (scanned-page rule, OCR edge cases), Q-25 (T2 hold codes, parts must agree, file-name last-four wins), Q-26 (card sign; T2 without opening/closing is held), **Q-27 (posting re-runs T2 — decide before M6 whether to reuse the reviewed `rows.json` by PDF hash)**.
2. **Behaviour changes this session:** `extractor-not-available` is gone (PDFs now post after G1); a CSV/XLSX account column that contradicts the file-name last-four now holds `conflicting-last4`.
3. **M4 hints:** FR-5 says invoice files → text (PdfText/Ocr; images → Ocr) → Hermes T3. `PdfText` and `IOcr` are ready; images (`.png/.jpg`) go straight to `IOcr.ReadImageAsync` when enabled, else the plan says `unreadable`. Follow the T2 pattern: `Hermes/prompts/invoice.md`, an `InvoiceAnswer : IValidatable`, add `HermesTask.Invoice` to the required prompts in `Program.cs`, hold on `HermesException`. `tests/fixtures/hermes/invoice.json` already exists (FakeHermesClient serves it). The sample invoice is still a `.txt` stand-in reported `unsupported-extension` (Q-20); M4 needs a real PDF fixture — render one like `tests/fixtures/statements/*.pdf` (throw-away PdfPig program).
4. **Test helpers:** `ScriptedHermes` (Core) validates like the real client; `TestStatementReader.Create(ocr, hermes)` builds the pipeline's reader; `JobPipelineTests` switches statements to PDFs with `UsePdf(name)` and scripts T2 via `_statementAnswer` / `_cardAnswer`.
5. **Already true from earlier sessions:** JSON enums are camelCase; read shared JSON with `AtomicFile.ReadAllText`; `ApiFactory.WithSetting(key, value)` (not chainable — use `WithWebHostBuilder` for several keys); console logs lack the `jobId` scope until T-702; sample output and `src/QbAutopost.Api/data/` are git-ignored; `.gitattributes` marks `*.pdf *.xlsx *.png *.jpg` binary.
6. **Running by hand:** `dotnet run` needs `ASPNETCORE_ENVIRONMENT=Development` (or a real `QBAUTOPOST__Api__ApiKey`). With `Ocr:Enabled=true`, set `Ocr:TessDataPath` to a folder holding `eng.traineddata`.
7. **GateGuard hook** blocks the first edit/creation of every file until facts are stated (retry works); `ECC_GATEGUARD=off` disables it.

## 6. Next milestone — M4 (invoices)

See `docs/plan.md` T-401…T-404 and spec FR-5 (T3 schema §9.3; matching by amount ±0.005 and date ±`InvoiceMatchDays`; ambiguous → no match; unmatched → `result.json.unmatchedInvoices[]`).

Open follow-ups: run `dotnet test` on Linux (a CI workflow would do); decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy; push sessions 2–4; answer Q-27 before M6.
