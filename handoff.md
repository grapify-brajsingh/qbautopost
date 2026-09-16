# Session Handoff — QbAutopost

Written: 2026-09-17 (session 7) · M0–M5 done · Next step: **M6** (QuickBooks gateway, post, undo), starting with **T-601**

## 1. Start the next session with this prompt

```
Read handoff.md, CLAUDE.md, docs/tracker.md and docs/adr/README.md.
Check the tracker "Questions" table for owner answers (Q-1…Q-33) and apply any
that change behaviour first. Then continue with the first todo task in M6 (T-601).
One task at a time, tests green, tracker updated, one commit per task.
```

## 2. Project in one paragraph

QbAutopost is a single .NET 8 app. It takes a job folder (`requirement.txt` plus bank/card statements plus optional invoices) and turns it into QuickBooks Desktop transactions: Checks, credit-card charges and credits, and Deposits. Hermes, an OpenAI-compatible model API on `127.0.0.1:8642`, handles reading and judgment. Deterministic code handles all money. The contract is `docs/spec.md`, the milestones are in `docs/plan.md`, progress is in `docs/tracker.md`, and the agent rules are in `CLAUDE.md`.

## 3. State at handoff

| Item | State |
|---|---|
| Repo | `D:\qb_post`, branch `main`, remote `origin` = https://github.com/grapify-brajsingh/qbautopost.git |
| Push | Session 2–7 commits (T-101…T-504) are **local only**. Push when the owner agrees: `git push origin main` |
| SDK | pinned to 8.0.x by `global.json` |
| Build | `dotnet build -warnaserror` → 0 warnings, 0 errors |
| Tests | 664 passing (Core 583, Api 81) on Windows. **Not yet run on Linux.** |
| Milestones | M0–M5 done; M6–M8 todo |
| Packages | none added in M5 |
| Manual check | None this session. There is no Hermes on the dev box, so a real job still fails at T1 (Q-22) |

## 4. What M5 added (code map delta)

```
src/QbAutopost.Core/
  Hermes/AccountAnswer.cs, Hermes/prompts/account.md   T4 DTO (account non-blank, 0 ≤ confidence ≤ 1), prompt
  Abstractions/IHermesClient.cs   HermesRequest.Check: caller-supplied validation run inside the client's one retry
  Mapping/AccountChooser.cs       AccountQuestion/AccountChoice/AccountChoiceResult; §9.4 type filter (ChoosableAccounts),
                                  account ∈ list (ordinal), top-3 listed candidates; holds no-accounts / hermes-failed
  Mapping/ModelTiers.cs           async pass after the job gates over lines held no-account-rule: tier 3 (vendor invoice
                                  with hint, score ≥ threshold → Invoice) else tier 4 with the same answer → G3
  Gates/ConfidenceGate.cs         G3: Model posts only with score ≥ threshold AND a live ledger entry payee+lineAccount
  Models/MappedTxn.cs             + ModelConfidence (T4 score)
  Models/HoldReasons.cs           no-accounts, low-confidence, no-prior-posting
  Mapping/Rules.cs                ModelConfidenceThreshold must be in (0, 1] (else the job fails)
  Output/OutputDocuments.cs       AnalysisLine.ModelConfidence (analysis.json "modelConfidence")
  Pipeline/JobPipeline.cs         new AccountChooser ctor argument; ModelTiers after ApplyJobGates
src/QbAutopost.Api/Program.cs     account.md required at startup; AccountChooser registered
tests/  Hermes/AccountAnswerTests, Mapping/{AccountChooserTests,ModelTiersTests}, Gates/ConfidenceGateTests,
        Output/AnalysisLineTests, TestSupport/TestAccountChooser; JobPipelineTests (+tier cases, _rulesFile/UseRules);
        Api/AccountTierApiTests (qb-lists seeded in the factory's data dir)
        fixtures/output/sample-analysis.golden.json gains "modelConfidence": null per line
```

## 5. Things the next session must know

1. **No owner answers yet.** Q-1…Q-33 all use the conservative choices. New in M5: Q-31 (T4 list filter, unlisted alternatives dropped, no accounts → `no-accounts`), Q-32 (tier-3 answer below the threshold falls to tier 4 without a second call; only vendor invoices with a hint count; G3 hold codes; payee/account compared ignoring case; threshold range), Q-33 (`confidence` stays the category, the score is `modelConfidence`). **Q-27 now also covers T4:** posting re-runs the analysis, so T4 is called again at post time and may answer differently from the reviewed dry run. Ask the owner before M6 posting work if possible.
2. **Behaviour changes in M5:** lines that tiers 1–2 leave open are no longer held `no-account-rule` when `qb-lists.json` has accounts. They go to Hermes T4. Without synced accounts they are held `no-accounts` and Hermes is not called. The kind gate (`kind-not-requested`), duplicates and `already-posted` run **before** T4, so those lines never reach Hermes. The sample job is unchanged: no qb-lists means no T4 call, and the decisions are still 8 post, 2 hold, 1 skip. Tests that count Hermes calls filter by task.
3. **M6 hints:**
   - `src/QbAutopost.QuickBooks` holds only a `.csproj` so far (CA1416 is suppressed there only). The POC is not available (ADR-0007), so write `QbSession` from spec FR-11: late-bound `QBXMLRP2.RequestProcessor`, `OpenConnection2("", AppName, 1)`, `BeginSession(file, 2)`, `ProcessRequest`, `EndSession`, `CloseConnection`. Put `[SupportedOSPlatform("windows")]` on it. `CoreIsolationTests` forbids COM types in Core.
   - `Program.cs:63` registers `UnconfiguredQbGateway` (`TODO(T-601)`). T-601 adds the DI switch: the fake gateway when `QuickBooks:Fake=true` or when not on Windows. `FakeQbGateway` exists only in `Api.Tests/TestSupport`. The Api project would need its own fake, or the switch keeps `UnconfiguredQbGateway` for non-Windows. Decide, then record the choice as a Q-row.
   - `JobPipeline.PostAsync` already posts, verifies (G5 `PostVerifier`), writes `response.qbxml` and records the ledger (Q-18 covers failure handling). `TODO(T-603, T-604)` in that method marks where the live G4 query, retry-once, busy timeout and backup-age guard go. T-605 is partly done. Confirm it against FR-12/§13 and close it.
   - `FakeQbGateway` already supports `RejectLine`, `Throw` and `Hang`. `PostingApiTests` covers the basic post path.
   - `QbXmlBuilder`/`QbXmlParser` exist (FR-9 golden files). Query and delete (`TxnDelRq`) builders and parsers are new work, so add golden files for them.
   - T-602 sync-lists writes `qb-lists.json` through `QbListsStore`. Keep the `QbAccount.Type` values because `AccountChooser` filters on them.
   - T-609 is **[server]**: prepare the checklist and set the row to `ready-for-human`, never `done`.
4. **Test helpers:** `ScriptedHermes` (Core) validates like the real client and applies `Check`. `TestStatementReader`, `TestInvoiceExtractor` and `TestAccountChooser` build the pipeline's readers. In `JobPipelineTests`, `_accountAnswer`, `_invoiceAnswer` and `_rulesFile` (via `UseRules`) script a run, and `UsePlumberInvoiceAndAccounts()` gives the 3199.70 line a known payee plus synced accounts. In Api tests, use `FakeHermesClient.Respond(r => r.Task == … ? json : null)` and seed `_factory.Dir.Combine("data", "qb-lists.json")` before the host starts.
5. **Still true from earlier sessions:**
   - JSON enums are camelCase.
   - Read shared JSON with `AtomicFile.ReadAllText`.
   - `ApiFactory.WithSetting(key, value)` is not chainable. Use `WithWebHostBuilder` when you need several keys.
   - Console logs lack the `jobId` scope until T-702.
   - Sample output and `src/QbAutopost.Api/data/` are git-ignored.
   - `.gitattributes` marks `*.pdf *.xlsx *.png *.jpg` as binary.
   - A golden mismatch writes `sample-analysis.actual.json` next to the test assembly. Diff it, then copy it over the golden file.
6. **Running by hand:** `dotnet run` needs `ASPNETCORE_ENVIRONMENT=Development` or a real `QBAUTOPOST__Api__ApiKey`. With `Ocr:Enabled=true`, set `Ocr:TessDataPath` to a folder that holds `eng.traineddata`.
7. **GateGuard hook:** it may block the first edit or creation of a file until you state the facts. Retrying works.

## 6. Next milestone: M6 (QuickBooks gateway, post, undo)

See `docs/plan.md` T-601…T-609 and these spec sections: FR-8 (G4 live query), FR-11 (session, retry, busy, backup guard), FR-12/§13 (G5, ledger), FR-13 (undo), FR-15 (sync-lists), FR-16 (health), FR-17 (query audit files).

Open follow-ups:
- Run `dotnet test` on Linux (a CI workflow would cover this).
- Decide whether `docs/CLAUDE.md` or the root `CLAUDE.md` is the single copy.
- Push sessions 2–7.
- Get the owner's answer to Q-27 (re-running T2/T3/T4 at post time) before posting goes live.
