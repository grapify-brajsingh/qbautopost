# Dashboard API M12 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move every model call from Hermes to OpenRouter. Under `/api/v2`, serve what the dashboard's Home page and
manual PDF upload need (R-10 … R-14). `/api/v1/**` stays byte-identical.

**Architecture:**
- **Core.** Pure rules go in `QbAutopost.Core`:
  - time buckets, percentiles, local-date bounds;
  - the extraction answer, tidy-up and file-name rules;
  - the upload PDF reader, the upload duplicate matcher and the upload version builder;
  - the summary builder.
- **Data.** `QbAutopost.Data` gets one migration (`DashboardM12`) with:
  - the `statement_extractions` table;
  - the upload columns;
  - three indexes.
  It also gets two new store interfaces (`IUploadStore`, `IDashboardSummaryStore`). Each is implemented by the EF
  store, `FakeSavedRequestStore` and `DisabledSavedRequestStore`, and covered by one contract suite run against both
  real stores.
- **Api.** The existing `HermesClient` is pointed at OpenRouter through a new `OpenRouter` settings section. Three new
  v2 routes sit in the existing `/api/v2` group.

**Tech Stack:** .NET 8 minimal APIs, EF Core 8 + Npgsql, PdfPig, xUnit, WebApplicationFactory,
Testcontainers.PostgreSql. **No new packages.**

**Spec:** `docs/dashboard-api-m12.md` (every `§` below refers to it). `v2 §n` refers to
`docs/saved-requests-dashboard-v2.md`.
**Branch:** `feature/m12-home-upload-openrouter` · **Milestone:** M12 · **Tasks:** T-1201 … T-1215

---

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` are green on Windows and Linux after **every** task. Data tests skip
  cleanly without Docker.
- **No v1 change:**
  - `src/QbAutopost.Api/wwwroot/openapi.json` stays byte-identical (`V1ContractPinTests`).
  - No existing test file is edited or deleted, except the deploy-script tests named in T-1203.
  - Every new parameter on a shared type defaults to today's behaviour.
- **New tests go in new files**, named for the feature (for example `V2SummaryTests.cs`, `ExtractionTidyTests.cs`).
  Test names are `Should_<behaviour>_When_<condition>`.
- **No test contacts OpenRouter, QuickBooks or Hermes.** Model calls use `FakeHermesClient` or a fake
  `HttpMessageHandler`.
- Core gets no EF, ASP.NET, Npgsql or `HttpClient` registration.
- **Rule 3:**
  - `controlTotal` is never computed, rounded or filled; an extraction always returns `controlTotal: null`.
  - Amounts are read as `decimal` (never through `double`).
- **No defaults:** the tidy-up never writes a value the model did not return (§6.4).
- **Nothing persisted from the PDF:** no PDF bytes or PDF text in the database, `output/`, logs, audit copies or
  leftover temp files.
- **No secret leaves the server:** `OpenRouter:ApiKey` is never in `appsettings*.json`, a response, a log line, a
  fixture or an exception message.
- **No new timer or hosted service.** Expiry is in-line (§7.3).
- **Append-only:** no `UPDATE` or `DELETE` on `saved_request_versions`, `saved_request_events` or `user_audit`.
- **Every new v2 route:**
  - is named in `ApiRoutes.ScopeFor`;
  - is documented in `wwwroot/openapi-v2.json` in the same task that maps it;
  - is covered by the security-trap test.
- **Time bounds:** `from` inclusive, `to` exclusive for instants. Date-only `xTo` covers the whole day.
- **Each task ends the same way:**
  - update its `docs/tracker.md` row (Status, Files, Verified by);
  - add one Session-log line;
  - commit `T-12xx: <summary>` with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A PDF with a text page and one short page** (a cover page, a scanned signature page). Expected: it is read,
   because `no-text` needs every page to be short. Pinned in T-1207
   (`Should_ReadText_When_OnlySomePagesAreShort`).
2. **A day bucket across a DST change** (`America/New_York`, 2026-11-01). Expected: one bucket per local day, 25 h
   long, and `series` still sums to `counts`. Pinned in T-1205
   (`Should_MakeOneBucketPerLocalDay_When_RangeCrossesDst`).
3. **The model returns an amount as a string (`"1,240.00"`) or a negative number.** Expected:
   - the string → `amount` omitted, row kept;
   - the negative → absolute value, otherwise unchanged.
   Pinned in T-1206 (`Should_OmitAmount_When_NotANumber`, `Should_MakeAmountPositive_When_Negative`).
4. **A save of an upload that lost its response and is retried with the same `Idempotency-Key`.** Expected: the
   original `201` replayed, not `409`. Pinned in T-1213 (`Should_ReplayCreated_When_SameKeyAndBodyRetried`).
5. **A request body of 3 MB sent to an existing route such as `POST /api/v2/saved-requests`.** Expected: still refused
   at 2 MB with today's problem; only the extraction route allows 10 MB. Pinned in T-1212
   (`Should_KeepGlobalLimit_When_RouteIsNotExtraction`).

---

## File structure

| Area | New files |
|---|---|
| Core, model client | `src/QbAutopost.Core/Hermes/ResponseFormat.cs` |
| Core, dashboard | `src/QbAutopost.Core/Dashboard/{TimeBuckets,Percentiles,DateBounds,DashboardSummary,DashboardSummaryBuilder,AttemptResultRows}.cs` |
| Core, extraction | `src/QbAutopost.Core/Extraction/{ExtractionAnswer,ExtractionTidy,UploadFileName,UploadPdfReader,UploadDuplicateMatcher,UploadVersionBuilder,StatementExtraction,IUploadStore}.cs` |
| Core, summary store | `src/QbAutopost.Core/Dashboard/IDashboardSummaryStore.cs` |
| Data | `src/QbAutopost.Data/Entities/StatementExtractionEntity.cs`, `Configurations/StatementExtractionConfiguration.cs`, `EfSavedRequestStore.Upload.cs`, `EfSavedRequestStore.Summary.cs`, migration `*_DashboardM12.cs` |
| Api | `src/QbAutopost.Api/Configuration/{OpenRouterSettings,ExtractionSettings}.cs`, `src/QbAutopost.Api/Extraction/{extraction-prompt.json,ExtractionPrompt,ExtractionLimiter,StatementExtractionService}.cs`, `src/QbAutopost.Api/Endpoints/V2/{DashboardSummaryEndpoints,StatementExtractionEndpoints}.cs` |
| Tests, fakes | `tests/QbAutopost.Api.Tests/TestSupport/{FakeSavedRequestStore.Upload,FakeSavedRequestStore.Summary,FakeOpenRouterHandler}.cs` |
| Fixtures | `tests/fixtures/extractions/` (hand-written replies only; **no real client PDF**) |
| Docs | `docs/adr/0011-openrouter-for-model-calls.md`, `docs/testing/T-1215.server-checklist.md` |

---

### Task 1 (T-1201): Tracker, questions, differences

**Files:**
- Modify: `docs/tracker.md`, `docs/saved-requests-dashboard-v2.md` (§14 only)

- [ ] **Step 1:** Add milestone row M12 and section `## M12 — Home, upload, OpenRouter`:
  - contract `docs/dashboard-api-m12.md`, plan this file;
  - rows T-1201 … T-1215, all `todo`, with T-1215 tagged **[server]**.
- [ ] **Step 2:** Add questions **Q-105 … Q-110** to Questions, copied from spec §12 with their "choice until
  answered". Add owner decisions **M-1 … M-5** (2026-10-28) to Decisions.
- [ ] **Step 3:** Append to the v2 spec's §14 table the row for `to` being exclusive (spec §3.2) and a pointer to M12
  §11.
- [ ] **Step 4:** Run `dotnet build -warnaserror` and `dotnet test`. Expected: green, with no code change.
- [ ] **Step 5:** Commit `T-1201: M12 tracker, questions Q-105…Q-110, decisions M-1…M-5`.

### Task 2 (T-1202): OpenRouter settings replace Hermes; `response_format` in the client

**Files:**
- Create:
  - `src/QbAutopost.Api/Configuration/OpenRouterSettings.cs`
  - `src/QbAutopost.Core/Hermes/ResponseFormat.cs`
- Modify:
  - `src/QbAutopost.Api/Configuration/AppSettings.cs` (remove `HermesSettings`, add `OpenRouter`)
  - `appsettings.json` (remove `Hermes`, add `OpenRouter`)
  - `Program.cs` (bind `HermesOptions` from `OpenRouter`)
  - `Logging/{LoggingSetup,StartupLog}.cs`
  - `Security/SecretWarnings.cs`
  - `Health/AppHealth.cs` (v1 field `hermes` keeps its name and now reads `OpenRouter.Enabled`)
  - `src/QbAutopost.Core/Abstractions/IHermesClient.cs` (`HermesRequest`)
  - `src/QbAutopost.Core/Hermes/{HermesClient,HermesOptions}.cs`
- Test (new):
  - `tests/QbAutopost.Api.Tests/Configuration/OpenRouterSettingsTests.cs`
  - `tests/QbAutopost.Core.Tests/Hermes/HermesClientResponseFormatTests.cs`

**Interfaces:**
- Produces:
  - `OpenRouterSettings { bool Enabled=false; string BaseUrl="https://openrouter.ai/api/v1"; string ApiKey="";
    string Model="~google/gemini-pro-latest"; int TimeoutSeconds=120; string? Referer; string? Title }` at
    `AppSettings.OpenRouter`.
  - `HermesOptions` gains `string? Referer`, `string? Title`. Its defaults become OpenRouter's `BaseUrl` and `Model`.
  - `sealed record ResponseFormat(string Name, JsonObject Schema, bool Strict)`.
  - `HermesRequest` gains two trailing optional parameters:
    - `ResponseFormat? Format = null`: when set, the chat body carries
      `response_format: { type: "json_schema", json_schema: { name, strict, schema } }`;
    - `string? Model = null`: a per-call model; null means `HermesOptions.Model`.
  - `HermesTask` gains `Extraction`.
  - Add the test `Should_UseRequestModel_When_ModelGiven`.

- [ ] **Step 1: Write failing tests.**
  - `Should_BindOpenRouterSection_When_Configured`.
  - `Should_FailStartup_When_BaseUrlIsHttpAndNotLoopback` (`http://example.com` → `OptionsValidationException`).
    `http://127.0.0.1:9` is accepted.
  - `Should_WarnOnce_When_HermesSectionPresent`: a `Hermes` section in config → one warning naming `OpenRouter`.
    `HermesOptions.BaseUrl` still comes from `OpenRouter`.
  - `Should_ScrubKey_When_OpenRouterKeyLogged`: marker key `sk-or-TESTMARKER123` never appears in captured log text.
  - `Should_WarnSecretInJson_When_OpenRouterKeyInAppsettings`.
  - `Should_SendResponseFormat_When_FormatGiven`: the fake handler sees the `json_schema` body.
  - `Should_OmitResponseFormat_When_FormatNull`: the body equals today's for an existing T2 request.
  - `Should_SendAttributionHeaders_When_RefererAndTitleSet`.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~OpenRouter|FullyQualifiedName~ResponseFormat"`.
  Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Validate `BaseUrl` with `IValidateOptions<AppSettings>`.
  - `StartupLog` prints `OpenRouter: {BaseUrl}, model {Model}, key set|not set`.
  - Every type keeps its name (Q-109). Update the doc comments of `IHermesClient` and `HermesClient` to say they are
    the OpenRouter client.
- [ ] **Step 4: Run** the full `dotnet test`. Expected: green, with every existing Hermes test unedited, and
  `V1ContractPinTests` green.
- [ ] **Step 5: Commit** `T-1202: OpenRouter settings replace Hermes; response_format in the model client`.

### Task 3 (T-1203): Docs, ADR, deploy scripts for the move

**Files:**
- Create: `docs/adr/0011-openrouter-for-model-calls.md` (supersedes 0005's endpoint)
- Modify:
  - `docs/spec.md` (glossary, §4 hosting, FR-2/3/5 wording only)
  - `CLAUDE.md` (opening line, rule 1)
  - `docs/adr/README.md`
  - `deploy/start-all.ps1`, `deploy/install-task.ps1` (no Docker or Hermes steps; the `-DockerMode` parameter is
    removed)
  - `deploy/hermes/README.md` (obsolete note)
  - `docs/runbook.md` (OpenRouter key, outbound HTTPS, stop Hermes)
- Test: the existing deploy-script tests under `tests/QbAutopost.Core.Tests/Architecture/` that assert Docker steps
  are updated to the new expectations. This is the one allowed existing-test edit; record it in the tracker row.

- [ ] **Step 1:** Write a failing test `Should_NotReferenceDocker_When_StartAllRead`: `start-all.ps1` contains no
  `docker` (case-insensitive) and still starts the API.
- [ ] **Step 2:** Run it. Expected: FAIL.
- [ ] **Step 3:** Edit the scripts and docs. The runbook says Docker is no longer needed.
- [ ] **Step 4:** Run `dotnet test`. Expected: green.
- [ ] **Step 5:** Commit `T-1203: docs, ADR-0011 and deploy scripts move from Hermes to OpenRouter`.

### Task 4 (T-1204): R-10, pin what Home relies on

**Files:**
- Test (new):
  - `tests/QbAutopost.Data.Tests/HomeContractStoreContract.cs` with `EfHomeContractStoreTests` and
    `FakeHomeContractStoreTests`
  - `tests/QbAutopost.Api.Tests/SavedRequests/V2HomeContractTests.cs`

No production change is expected. If a test fails, fix the store, and record the fix in the tracker row.

- [ ] **Step 1: Write the store contract tests.** The data set: 3 saves, 2 posted attempts, 1 failed (outcome
  `held`), 1 partial, discard → restore → discard, and 3 user-audit rows.
  - `Should_CountAllMatches_When_PageSizeIsOne`: `AuditEventCountAsync` equals the `items` count summed across every
    page.
  - `Should_MatchAnyAction_When_ActionRepeated`.
  - `Should_EmitOneReceivedPerSave_When_Saved`.
  - `Should_EmitOnePostedPerPostedAttempt_When_Finished`: `atUtc` = the finish event time.
  - `Should_CountPartialAsPostFailed_When_AttemptPartial`.
  - `Should_EmitTwoDiscarded_When_DiscardRestoreDiscard`.
  - `Should_ExcludeUserEventsFromTotal_When_CallerLacksUsersManage`.
  - `Should_ReturnEachEventOnce_When_PagingBy200`.
  - `Should_ExcludeEventAtTo_When_ToEqualsEventTime`: `to` is exclusive.
- [ ] **Step 2: Write the API test** `Should_ReturnOldestFirst_When_QueueListSortedByReceived`: the R-10.6 query,
  with `received` falling back to `created`.
- [ ] **Step 3: Run** `dotnet test --filter "FullyQualifiedName~HomeContract"`. Expected: PASS. If anything fails,
  fix it and note the fix.
- [ ] **Step 4: Commit** `T-1204: pin the audit-feed contracts the Home page relies on (R-10)`.

### Task 5 (T-1205): Time buckets, percentiles, date bounds (Core)

**Files:**
- Create: `src/QbAutopost.Core/Dashboard/{TimeBuckets,Percentiles,DateBounds}.cs`
- Test (new): `tests/QbAutopost.Core.Tests/Dashboard/{TimeBucketsTests,PercentilesTests,DateBoundsTests}.cs`

**Interfaces:**
- Produces:
  - `enum BucketSize { Day, Week }`.
  - `sealed record TimeBucket(DateOnly Start, DateTime FromUtc, DateTime ToUtc)`.
  - `TimeBuckets.Build(DateTime fromUtc, DateTime toUtc, TimeZoneInfo tz, BucketSize size) : IReadOnlyList<TimeBucket>`.
    Every bucket that overlaps `[from, to)`, clipped to it, in order. Weeks start Monday.
  - `TimeBuckets.IndexOf(IReadOnlyList<TimeBucket> buckets, DateTime atUtc) : int` (−1 outside).
  - `Percentiles.NearestRank(IReadOnlyList<int> sorted, int percent) : int`: rank `ceil(p/100 × n)`, 1-based.
    Throws on an empty list.
  - `DateBounds.TryParse(string raw, TimeZoneInfo? tz, bool upper, out DateTime utc) : bool`:
    - a plain `yyyy-MM-dd` gives local midnight of that day (or of the next day when `upper`) in `tz`, or in UTC when
      `tz` is null;
    - an ISO instant with an offset is used as is;
    - the result is always UTC, inclusive for lower bounds and exclusive for upper ones.
  - `DateBounds.TryZone(string? id, out TimeZoneInfo? tz) : bool` (null/empty → UTC; unknown → false).

- [ ] **Step 1: Write failing tests.**
  - `Should_PutEventOnLocalDay_When_TzIsKolkata`: 2026-10-27T20:00Z → bucket `2026-10-28` (Asia/Kolkata), and
    `2026-10-27` for UTC.
  - `Should_MakeOneBucketPerLocalDay_When_RangeCrossesDst`: America/New_York 2026-10-31 … 2026-11-03 → 3 buckets, and
    the one starting `2026-11-01` is 25 h long.
  - `Should_IncludeEmptyBuckets_When_NoEvents`.
  - `Should_ClipFirstAndLastBucket_When_RangeStartsMidDay`.
  - `Should_StartWeekOnMonday_When_BucketIsWeek`: 2026-10-28 (a Wednesday) → `2026-10-26`.
  - `Should_ReturnNearestRank_When_Odd` ([10,20,30], p50 → 20, p90 → 30).
  - `Should_ReturnNearestRank_When_Even` ([1,2,3,4], p50 → 2).
  - `Should_ResolveLocalDay_When_TzGiven`: `2026-10-27`, Asia/Kolkata → lower 2026-10-26T18:30Z, upper
    2026-10-27T18:30Z.
  - `Should_KeepUtcDay_When_NoTz`.
  - `Should_UseInstantAsIs_When_OffsetGiven`.
  - `Should_RejectUnknownZone_When_TzInvalid`.
- [ ] **Step 2: Run** `dotnet test tests/QbAutopost.Core.Tests --filter "FullyQualifiedName~Dashboard"`. Expected:
  FAIL.
- [ ] **Step 3: Implement.** Build buckets by stepping local dates and converting each local midnight with
  `TimeZoneInfo.ConvertTimeToUtc`. For a midnight that does not exist (a DST gap), use the first valid local time
  after it.
- [ ] **Step 4: Run.** Expected: PASS on Windows and Linux (IANA ids via .NET 8 ICU).
- [ ] **Step 5: Commit** `T-1205: time buckets, nearest-rank percentile, local date bounds`.

### Task 6 (T-1206): Extraction answer, tidy-up, file name (Core)

**Files:**
- Create: `src/QbAutopost.Core/Extraction/{ExtractionAnswer,ExtractionTidy,UploadFileName}.cs`
- Test (new):
  - `tests/QbAutopost.Core.Tests/Extraction/{ExtractionAnswerTests,ExtractionTidyTests,UploadFileNameTests}.cs`
  - fixtures `tests/fixtures/extractions/{chase-checking-4521.reply.json,not-a-statement.reply.json,messy-rows.reply.json}`
    (hand-written)

**Interfaces:**
- Produces:
  - `sealed class ExtractionAnswer : IValidatable` with:
    - `[JsonPropertyName("is_bank_statement")] bool? IsBankStatement`;
    - `string? Reference`;
    - `List<JsonElement>? Transactions`.
    - `Validate()` reports: `is_bank_statement` missing; `transactions` missing or not an array; an item that is not
      an object.
  - `ExtractionTidy.Tidy(ExtractionAnswer answer) : JsonObject`, giving
    `{ reference: string|null, controlTotal: null, transactions: [...] }` by the §6.4 table.
  - `UploadFileName.Sanitize(string? raw) : string` (§6.8).

- [ ] **Step 1: Write failing tidy tests** (one behaviour each):
  - `Should_LeaveFieldOut_When_ModelOmitted` (no `account` → no `account` key);
  - `Should_LeaveFieldOut_When_ModelReturnedBlank` (`"payee": "  "`);
  - `Should_NeverWriteN8nDefaults_When_FieldsMissing`: the output JSON text contains none of `Capital One - 3188`,
    `6891`, `Uncategorized`, `Ask My Accountant`, `zemen-2019-12-p01`;
  - `Should_SetDeposit_When_KindIsDepositAnyCase`;
  - `Should_SetCheck_When_KindIsAnythingElse`;
  - `Should_SetCheck_When_KindCheckAndAmountPositive` (the n8n bug is not copied);
  - `Should_MakeAmountPositive_When_Negative` (−60.00 → 60.00);
  - `Should_OmitAmount_When_NotANumber` (`"1,240.00"`);
  - `Should_KeepAmountDigits_When_Decimal` (1240.10 stays 1240.10; read with `GetDecimal`);
  - `Should_RenumberExternalId_When_ModelGaveOwn` (`row-9`, `x` → `row-1`, `row-2`);
  - `Should_SetControlTotalNull_When_Tidied`;
  - `Should_SetReferenceNull_When_Blank`;
  - `Should_DropRefNumber_When_Present`.
- [ ] **Step 2: Write failing answer and file-name tests:**
  - `Should_FailValidation_When_IsBankStatementMissing`;
  - `Should_StripPathAndControlChars_When_Sanitized` (`..\\a\u0007b.pdf` → `ab.pdf`);
  - `Should_TruncateWithoutSplittingSurrogate_When_Over255`;
  - `Should_DefaultToUploadPdf_When_NothingLeft`.
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~Extraction"`. Expected: FAIL.
- [ ] **Step 4: Implement**, then run. Expected: PASS.
- [ ] **Step 5: Commit** `T-1206: extraction answer, tidy-up without defaults, safe file name`.

### Task 7 (T-1207): Upload PDF reader (Core)

**Files:**
- Create: `src/QbAutopost.Core/Extraction/UploadPdfReader.cs`
- Test (new): `tests/QbAutopost.Core.Tests/Extraction/UploadPdfReaderTests.cs`. Fixtures are generated in the test
  with PdfPig's `PdfDocumentBuilder` (text pages, a short page, more than N pages), plus the existing
  `tests/fixtures/statements/chase-checking-4521.pdf`. An encrypted fixture is a small committed synthetic PDF,
  `tests/fixtures/extractions/encrypted.pdf`. *Builder's call* how to produce it.

**Interfaces:**
- Produces:
  - `enum UploadPdfOutcome { Ok, NotAPdf, Protected, TooManyPages, NoText }`.
  - `sealed record UploadPdfResult(UploadPdfOutcome Outcome, int PageCount, string Text)`.
  - `UploadPdfReader.Read(ReadOnlyMemory<byte> bytes, int maxPages) : UploadPdfResult`:
    - checks the `%PDF-` prefix first;
    - pages are joined with `PdfTextResult.PageBreak`;
    - a page under `PdfText.ScannedPageThreshold` non-space characters counts as empty, and `NoText` is returned only
      when every page does;
    - never calls OCR.

- [ ] **Step 1: Write failing tests:**
  - `Should_ReturnNotAPdf_When_PrefixMissing`;
  - `Should_ReturnNotAPdf_When_PdfPigCannotOpen` (`%PDF-` followed by garbage);
  - `Should_ReturnProtected_When_Encrypted`;
  - `Should_ReturnTooManyPages_When_OverLimit` (maxPages 2, 3 pages);
  - `Should_ReturnNoText_When_EveryPageShort`;
  - `Should_ReadText_When_OnlySomePagesAreShort`;
  - `Should_JoinPagesWithPageBreak_When_Ok`;
  - `Should_LeaveJobReaderUnchanged_When_UploadReaderAdded` (the existing `PdfTextTests` stay unedited and green).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement**, with no file system access.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `T-1207: upload PDF reader (bytes in, distinct outcomes, no OCR)`.

### Task 8 (T-1208): Upload duplicate matcher and version builder (Core)

**Files:**
- Create: `src/QbAutopost.Core/Extraction/{UploadDuplicateMatcher,UploadVersionBuilder}.cs`
- Test (new): `tests/QbAutopost.Core.Tests/Extraction/{UploadDuplicateMatcherTests,UploadVersionBuilderTests}.cs`

**Interfaces:**
- Produces:
  - `sealed record RowKey(string Kind, string Date, decimal Amount)`.
  - `sealed record UploadDuplicateCandidate(long Id, string? Reference, SavedRequestStatus Status, DateTime ReceivedUtc,
    DateTime CreatedUtc, string Source, string? UploadSha256, IReadOnlySet<string> RowFingerprints,
    IReadOnlyList<RowKey> RowKeys)`.
  - `sealed record UploadDuplicateHit(long Id, string? Reference, SavedRequestStatus Status, DateTime ReceivedUtc,
    string Source, IReadOnlyList<string> Reasons)`.
  - `UploadDuplicateMatcher.Match(string sha256, string? reference, IReadOnlySet<string> fingerprints,
    IReadOnlyList<RowKey> rowKeys, IEnumerable<UploadDuplicateCandidate> candidates, int max = 5) :
    IReadOnlyList<UploadDuplicateHit>`. Reasons are `same-file`, `same-reference`, `overlapping-rows`; results come
    newest `CreatedUtc` first.
  - `UploadDuplicateMatcher.RowKeysOf(JsonElement payload) : IReadOnlyList<RowKey>`: rows lacking `kind`, `date` or a
    numeric `amount` are skipped.
  - `UploadVersionBuilder.Version1(JsonObject extractionPayload, JsonElement body) : JsonObject`: the extraction's
    `transactions` with the body's `reference` and `controlTotal` (copied as JSON values, `null` stays `null`).
  - `UploadDuplicateReasons.SameFile = "same-file"`; the other two reasons reuse `DuplicateReasons`.

- [ ] **Step 1: Write failing tests:**
  - `Should_FlagSameFile_When_ShaMatchesSavedUpload`;
  - `Should_FlagSameReference_When_CaseDiffers`;
  - `Should_NotFlagReference_When_Blank`;
  - `Should_FlagOverlap_When_OneFingerprintMatches`;
  - `Should_FlagOverlap_When_HalfOfRowsMatchByKindDateAmount` (4 rows, 2 match one candidate);
  - `Should_NotFlagOverlap_When_MatchesSpreadOverCandidates` (4 rows, 1 match each in 2 candidates);
  - `Should_ReturnAtMostFiveNewestFirst_When_ManyMatch`;
  - `Should_ListAllReasons_When_SeveralApply`;
  - `Should_TakeBodyReferenceAndTotal_When_BuildingVersion1`;
  - `Should_KeepNullControlTotal_When_BodyNull`;
  - `Should_IgnoreBodyTransactions_When_BuildingVersion1`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement**, then run. Expected: PASS.
- [ ] **Step 4: Commit** `T-1208: upload duplicate matcher and version-1 builder`.

### Task 9 (T-1209): Migration `DashboardM12`, extraction records

**Files:**
- Create:
  - `src/QbAutopost.Core/Extraction/{StatementExtraction,IUploadStore}.cs`
  - `src/QbAutopost.Data/Entities/StatementExtractionEntity.cs`, `Configurations/StatementExtractionConfiguration.cs`
  - migration `*_DashboardM12.cs`
  - `EfSavedRequestStore.Upload.cs` (extraction methods only in this task)
  - `tests/QbAutopost.Api.Tests/TestSupport/FakeSavedRequestStore.Upload.cs`
- Modify:
  - `SavedRequestEntity.cs` and its configuration (7 upload columns, §7.1)
  - `QbAutopostDbContext.cs`
  - `src/QbAutopost.Api/Database/DisabledSavedRequestStore.cs`
  - `deploy/migrations.sql`, `deploy/grants.sql` (regenerated)
  - `Program.cs` (DI for `IUploadStore`)
- Test (new):
  - `tests/QbAutopost.Data.Tests/{ExtractionStoreContract,EfExtractionStoreTests,FakeExtractionStoreTests,DashboardM12MigrationTests}.cs`

**Interfaces:**
- Produces:
  - `sealed record StatementExtraction(Guid Id, string UserId, string FileName, long SizeBytes, string Sha256,
    int PageCount, string Model, string PromptVersion, JsonObject Payload, DateTime ExtractedUtc, DateTime ExpiresUtc,
    long? SavedRequestId)`.
  - `interface IUploadStore`:
    - `Task CreateExtractionAsync(StatementExtraction e, CancellationToken ct)`;
    - `Task<int> DeleteExpiredExtractionsAsync(DateTime nowUtc, CancellationToken ct)` (unsaved only);
    - `Task<StatementExtraction?> GetExtractionAsync(Guid id, CancellationToken ct)`.
    T-1210 and T-1211 add more methods.
  - Indexes:
    - `ix_saved_request_events_kind_at` (`kind`, `at_utc desc`);
    - `ix_saved_requests_upload_sha256`;
    - `ix_statement_extractions_expires` (`expires_utc`, where `saved_request_id is null`).
  - Grants: the app role gets `SELECT, INSERT, UPDATE, DELETE` on `statement_extractions` only.

- [ ] **Step 1: Write failing contract tests:**
  - `Should_RoundTripExtraction_When_Created`;
  - `Should_DeleteOnlyUnsavedExpired_When_DeletingExpired` (one expired unsaved, one expired saved, one live →
    returns 1);
  - `Should_ReturnNull_When_ExtractionUnknown`.
- [ ] **Step 2: Write failing migration tests:**
  - `Should_CreateTableColumnsAndIndexes_When_Migrated`;
  - `Should_GrantOnlyExtractionTable_When_GrantsApplied` (extends the grants drift check);
  - `Should_KeepV1Isolation_When_Migrated` (the existing `EfV1IsolationStoreTests` stay green).
- [ ] **Step 3: Run.** Expected: FAIL (they skip without Docker; run with Docker).
- [ ] **Step 4: Implement.** Generate the migration with `dotnet ef migrations add DashboardM12`, then regenerate the
  SQL scripts the way `MigrationScriptTests` expects.
- [ ] **Step 5: Run** the Data and Api suites. Expected: PASS.
- [ ] **Step 6: Commit** `T-1209: DashboardM12 migration, extraction records, upload columns, indexes`.

### Task 10 (T-1210): Store: save an upload in one transaction; upload duplicate candidates

**Files:**
- Modify:
  - `src/QbAutopost.Core/Extraction/IUploadStore.cs`
  - `src/QbAutopost.Data/EfSavedRequestStore.Upload.cs`
  - `FakeSavedRequestStore.Upload.cs`
  - `DisabledSavedRequestStore.cs`
  - `src/QbAutopost.Core/SavedRequests/Audit/AuditEventMapper.cs` (pass the `created` detail through)
  - `SavedRequestModels.cs` (`SavedRequest.Upload`, `SavedRequestSummary.Upload`)
  - the list `q` filter in both stores (adds `upload_file_name`)
- Test (new): `tests/QbAutopost.Data.Tests/{UploadSaveStoreContract,EfUploadSaveStoreTests,FakeUploadSaveStoreTests}.cs`

**Interfaces:**
- Consumes: `NewSavedRequest`, `CreateResult` (existing); `StatementExtraction` (T-1209); `UploadDuplicateCandidate`
  (T-1208).
- Produces:
  - `sealed record UploadMeta(string FileName, long SizeBytes, string Sha256, string Model, string PromptVersion,
    DateTime ExtractedUtc)`, exposed as `SavedRequest.Upload` / `SavedRequestSummary.Upload` (`UploadMeta?`).
  - `sealed record NewUploadSave(NewSavedRequest Request, Guid ExtractionId, string UserId, JsonElement Version1Payload,
    JsonElement? Version2Payload, JsonArray? Version2Changes, string Version1Note, string Version2Note)`:
    - `Request.Payload` is the **current** version, i.e. version 2 when present, otherwise version 1;
    - the read, hash, fingerprints, snapshot and duplicate result are all computed on it.
  - `enum UploadSaveKind { Created, Replayed, Expired, NotFound, AlreadySaved }`.
  - `sealed record UploadSaveResult(UploadSaveKind Kind, SavedRequest? Record, long? SavedRequestId, string? PublicRef)`.
  - `IUploadStore.SaveUploadAsync(NewUploadSave save, CancellationToken ct) : Task<UploadSaveResult>`. Order inside
    one transaction:
    1. idempotency replay, as in `CreateAsync` → `Replayed`;
    2. lock the extraction `FOR UPDATE`;
    3. unknown or expired → delete it if unsaved, `Expired`;
    4. `UserId` differs → `NotFound`;
    5. already saved → `AlreadySaved` with its id and ref;
    6. insert the request with the upload columns copied from the record;
    7. write version 1, and version 2 when present, with the same time and actor;
    8. write the `created` event with detail `{ source: "upload", fileName }`;
    9. set `saved_request_id`.
  - `IUploadStore.UploadDuplicateCandidatesAsync(string sha256, DateTime sinceUtc, CancellationToken ct) :
    Task<IReadOnlyList<UploadDuplicateCandidate>>`: every saved request since `sinceUtc`, plus any request whose
    `upload_sha256` equals `sha256` regardless of date. `RowKeys` come from the current version's payload.

- [ ] **Step 1: Write failing contract tests:**
  - `Should_WriteVersion1Only_When_NoRowChanges`;
  - `Should_WriteVersion2WithChanges_When_RowsEdited`;
  - `Should_CopyUploadColumnsFromRecord_When_Saved`;
  - `Should_WriteCreatedDetail_When_UploadSaved` (and the audit feed shows `detail.source == "upload"` in both
    stores);
  - `Should_ReturnAlreadySaved_When_SavedTwice`;
  - `Should_ReturnExpiredAndDelete_When_Expired`;
  - `Should_ReturnNotFound_When_OtherUser`;
  - `Should_ReplayCreated_When_SameIdempotencyKey`;
  - `Should_WriteNothing_When_AlreadySaved` (counts of requests, versions and events unchanged);
  - `Should_MatchFileName_When_QContainsIt`;
  - `Should_ReturnShaMatchOutsideWindow_When_CandidatesLoaded`;
  - `Should_ReturnOneSavedRequest_When_TwoSavesRace` (two parallel saves of one extraction → one `Created`, one
    `AlreadySaved`).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Data and Api suites. Expected: PASS, with the existing `AuditEventsStoreContract`
  unchanged and green.
- [ ] **Step 5: Commit** `T-1210: save an upload with both versions in one transaction; upload candidates`.

### Task 11 (T-1211): Store and Core: summary facts, summary builder, posted filters

**Files:**
- Create:
  - `src/QbAutopost.Core/Dashboard/{IDashboardSummaryStore,DashboardSummary,DashboardSummaryBuilder,AttemptResultRows}.cs`
  - `src/QbAutopost.Data/EfSavedRequestStore.Summary.cs`
  - `FakeSavedRequestStore.Summary.cs`
- Modify:
  - `SavedRequestV2Models.cs` (`SavedRequestQueryV2`)
  - `EfSavedRequestStore.V2.cs`, `FakeSavedRequestStore.V2.cs` (posted filters and sort; instant date bounds)
  - `DisabledSavedRequestStore.cs`
- Test (new):
  - `tests/QbAutopost.Core.Tests/Dashboard/{DashboardSummaryBuilderTests,AttemptResultRowsTests}.cs`
  - `tests/QbAutopost.Data.Tests/{SummaryStoreContract,EfSummaryStoreTests,FakeSummaryStoreTests,PostedFilterStoreContract,EfPostedFilterStoreTests,FakePostedFilterStoreTests}.cs`

**Interfaces:**
- Produces:
  - `sealed record EventFact(string Action, DateTime AtUtc)`.
  - `sealed record PostedFact(long AttemptId, DateTime FinishedUtc, string? ResultJson)`: the finish time comes from
    the `post-finished` event; outcomes are posted or partial.
  - `sealed record PostTimeFact(long SavedRequestId, DateTime ReceivedUtc, DateTime PostedUtc)`: `PostedUtc` is per
    `PostSummaries.Of`.
  - `interface IDashboardSummaryStore`:
    - `Task<IReadOnlyList<EventFact>> EventFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)`, for
      actions `received`, `posted`, `post-failed`, `discarded`, read from the same source as `audit_events`;
    - `Task<IReadOnlyList<PostedFact>> PostedFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)`;
    - `Task<IReadOnlyList<PostTimeFact>> PostTimeFactsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)`.
  - `AttemptResultRows.Read(string? resultJson) : (int Rows, decimal Amount)`: reads `posted[]` count and the sum of
    `posted[].amount`; `(0, 0m)` when absent.
  - `DashboardSummary` records matching the §4.2 JSON exactly: `Counts`, `Posted`, `TimeToPost?`, `SeriesEntry`.
  - `DashboardSummaryBuilder.Build(...)` takes:
    - `IReadOnlyList<TimeBucket> buckets`;
    - `IReadOnlyList<EventFact> events`, `IReadOnlyList<PostedFact> posted`, `IReadOnlyList<PostTimeFact> times`;
    - the same three lists for the previous range, all nullable.
    It returns `DashboardSummary`.
  - `SavedRequestQueryV2` gains optional members, each defaulting to null so existing callers behave as today:
    - `DateTime? ReceivedFromUtc`, `ReceivedToUtc`, `CreatedFromUtc`, `CreatedToUtc`, `PostedFromUtc`, `PostedToUtc`
      (lower bounds inclusive, upper bounds exclusive);
    - sort keys `posted` / `-posted` (no post sorts last, then `id desc`).

- [ ] **Step 1: Write failing Core tests:**
  - `Should_SumPostedArrayAmounts_When_ResultHasRows`;
  - `Should_ReturnZero_When_ResultNull`;
  - `Should_CountOnlyPostedEvents_When_PartialAttemptPresent` (`counts.posted` excludes partial; `posted.amount`
    includes it);
  - `Should_SumSeriesToCounts_When_Built`;
  - `Should_ReturnNullTimeToPost_When_NoPosts`;
  - `Should_ComputeMedianAndP90_When_Posts` (minutes [60, 120, 600] → 120, 600, `statements` 3);
  - `Should_ReturnNullPrevious_When_NoCompareRange`.
- [ ] **Step 2: Write failing store contract tests:**
  - `Should_ReturnEventFactsMatchingAuditCounts_When_RangeGiven`;
  - `Should_ReturnPostedAndPartialFacts_When_InRange`;
  - `Should_UseResolvedTime_When_RequestResolvedToPosted`;
  - `Should_FilterByPostedLocalDay_When_TzGiven` (spec §5 acceptance);
  - `Should_SortUnpostedLast_When_SortPosted`, in both directions;
  - `Should_MatchPostSummary_When_FilteringByPosted` (the filter's time equals `PostSummaries.Of` for every row);
  - `Should_BehaveAsBefore_When_OnlyDateOnlyFiltersSet`.
- [ ] **Step 3: Run.** Expected: FAIL.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** the Core, Data and Api suites. Expected: PASS.
- [ ] **Step 6: Commit** `T-1211: summary facts and builder; posted-time filters and sort in both stores`.

### Task 12 (T-1212): Api: `/dashboard/summary`, R-12 parameters, extraction route

This task carries the money-free routes and the upload endpoint because both use the new v2 query conventions. If a
reviewer wants to split it, the seam is between Steps 4 and 5.

**Files:**
- Create:
  - `src/QbAutopost.Api/Endpoints/V2/{DashboardSummaryEndpoints,StatementExtractionEndpoints}.cs`
  - `src/QbAutopost.Api/Configuration/ExtractionSettings.cs`
  - `src/QbAutopost.Api/Extraction/{extraction-prompt.json,ExtractionPrompt,ExtractionLimiter,StatementExtractionService}.cs`
  - `tests/QbAutopost.Api.Tests/TestSupport/FakeOpenRouterHandler.cs`
- Modify:
  - `V2QueryParser.cs` (`tz`, instant bounds, `postedFrom`/`postedTo`, `sort=posted`)
  - `ApiRoutes.cs` (`ScopeFor`: summary → `requests:read`; extractions → `requests:submit`)
  - `Security/RequestSizeMiddleware.cs` (per-route limit)
  - `Program.cs` (Kestrel limit = max of the two settings; DI)
  - `Health/AppHealth.cs` (v2 `extraction` and `model` entries only)
  - `wwwroot/openapi-v2.json`
  - `src/QbAutopost.Api/QbAutopost.Api.csproj` (embed the prompt file)
- Test (new):
  - `tests/QbAutopost.Api.Tests/SavedRequests/{V2SummaryTests,V2PostedFilterTests,V2ExtractionTests,ExtractionPromptTests,RequestSizeExtractionTests}.cs`

**Interfaces:**
- Consumes: `TimeBuckets`, `DateBounds`, `DashboardSummaryBuilder` (T-1205, T-1211); `UploadPdfReader` (T-1207);
  `ExtractionAnswer`, `ExtractionTidy`, `UploadFileName` (T-1206); `UploadDuplicateMatcher` (T-1208);
  `IUploadStore` (T-1209, T-1210); `IHermesClient` + `ResponseFormat` (T-1202).
- Produces:
  - `ExtractionSettings { bool Enabled=false; string Model="~google/gemini-pro-latest"; int TimeoutSeconds=120;
    long MaxBytes=10485760; int MaxPages=50; int MaxPerUserPerHour=20; int RecordHours=24 }` at
    `AppSettings.Extraction`.
  - `ExtractionPrompt.Load() : ExtractionPrompt`, with `string PromptVersion` (`2026-10-28.api-1`),
    `string SystemPrompt` and `JsonObject Schema`, loaded from the embedded `extraction-prompt.json`.
  - `ExtractionLimiter.TryStart(string userId, DateTime nowUtc, out TimeSpan retryAfter) : IDisposable?`: null when
    refused.
  - `StatementExtractionService.ExtractAsync(string userId, string fileName, ReadOnlyMemory<byte> bytes,
    CancellationToken ct) : Task<ExtractionOutcome>`. The result maps 1:1 to the §6.1 / §6.9 problems or the §6.8
    `200` body.
  - The OpenRouter call goes through `IHermesClient.CompleteJsonAsync<ExtractionAnswer>` with:
    - `HermesTask.Extraction`;
    - `AuditDir: null`;
    - `Format: new ResponseFormat("bank_statement", prompt.Schema, Strict: true)`;
    - `Model: settings.Extraction.Model` (all from T-1202).

- [ ] **Step 1: Write failing summary tests:**
  - `Should_ReturnSummaryShape_When_RangeGiven` (the §4.2 keys);
  - `Should_EqualAuditTotals_When_SameRange`;
  - `Should_Return400_When_ToNotAfterFrom`;
  - `Should_Return400_When_RangeOver366Days`;
  - `Should_Return400_When_OnlyCompareFromSent`;
  - `Should_Return400_When_TzUnknown`;
  - `Should_AllowEveryRole_When_RequestsRead`;
  - `Should_DocumentRoute_When_OpenApiV2Read`.
- [ ] **Step 2: Write failing R-12 API tests:**
  - `Should_FilterPostedLocalDay_When_TzKolkata`;
  - `Should_AcceptInstant_When_OffsetGiven`;
  - `Should_KeepUtcDates_When_NoTz` (the existing `V2ReadTests` stay unedited and green).
- [ ] **Step 3: Implement** the summary route and the parser, then run Steps 1–2. Expected: PASS.
- [ ] **Step 4: Commit** `T-1212a: GET /api/v2/dashboard/summary and posted/tz list filters`.
- [ ] **Step 5: Write failing extraction tests**, using `FakeOpenRouterHandler` serving `tests/fixtures/extractions/*`:
  - `Should_Return200WithTidiedPayload_When_StatementRead` (`chase-checking-4521.pdf` + reply fixture; asserts §6.4);
  - `Should_SendPromptSchemaModelAndText_When_Calling`: the handler captures the body, which contains the PDF's text,
    `ExtractionPrompt.SystemPrompt`, `response_format.json_schema.schema` equal to `ExtractionPrompt.Schema`, and
    `model == "~google/gemini-pro-latest"`;
  - `Should_Return422NotABankStatement_When_ModelSaysFalse`, with no record stored;
  - `Should_Return200EmptyRows_When_StatementHasNoRows`;
  - one test per row of the §6.1 table: `503`, `400`, `413` bytes, `415` prefix, `415` unopenable, `422` protected,
    `413` pages, `422 no-text`, `429` in flight, `429` hourly with `Retry-After`;
  - `Should_Return502_When_ReplyInvalidTwice`;
  - `Should_Return502_When_OpenRouterErrors` (no retry: the handler sees 1 call);
  - `Should_Return504_When_NoAnswerInTimeout` (fake clock / short timeout);
  - `Should_Return403_When_Viewer`;
  - `Should_Return403_When_ApiKeyClient`;
  - `Should_ListSameFile_When_SavedFileUploadedAgain`;
  - `Should_DeleteExpiredUnsaved_When_NewExtractionCreated`;
  - `Should_LeaveNoTraceOfPdfText_When_Extracted`: a generated PDF whose text includes marker `ZZMARKER-7731`, with
    a reply fixture that does not contain it. After the call, the store's records, `output/`, the temp directory and
    the captured logs contain no marker;
  - `Should_NeverExposeKey_When_AnyOutcome` (marker key absent from every response and log line);
  - `Should_KeepGlobalLimit_When_RouteIsNotExtraction`;
  - `Should_PinPromptFileHash_When_VersionUnchanged` (the SHA-256 of `extraction-prompt.json` is pinned together
    with `2026-10-28.api-1`).
- [ ] **Step 6: Write `extraction-prompt.json`** as `{ "promptVersion": "2026-10-28.api-1", "systemPrompt": …,
  "schema": … }`:
  - `systemPrompt` is the exact text of spec §6.3, with no ★ marks;
  - `schema` is spec Appendix A, verbatim.
- [ ] **Step 7: Implement** the endpoint, service, limiter, size rule, health entries and OpenAPI entries. The
  disconnect token cancels the model call.
- [ ] **Step 8: Run** the full `dotnet test`. Expected: green, with the v1 hash unchanged.
- [ ] **Step 9: Commit** `T-1212b: POST /api/v2/statement-extractions via OpenRouter`.

### Task 13 (T-1213): Api: v2 save with `upload`; `upload` on views

**Files:**
- Modify:
  - `src/QbAutopost.Api/SavedRequests/{SavedRequestIntake,SavedRequestViewsV2}.cs`
  - `src/QbAutopost.Core/SavedRequests/{SaveMetadata,PayloadRules}.cs` (`upload` is metadata in V2 rules)
  - `src/QbAutopost.Api/Endpoints/SavedRequestEndpoints.cs`
  - `wwwroot/openapi-v2.json`
- Test (new): `tests/QbAutopost.Api.Tests/SavedRequests/V2UploadSaveTests.cs`

**Interfaces:**
- Consumes: `UploadVersionBuilder` (T-1208); `IUploadStore.SaveUploadAsync`, `UploadSaveResult`, `UploadMeta`
  (T-1210).
- Produces: problems `410 extraction-expired`, `409 extraction-already-saved` (extensions `savedRequestId`,
  `publicRef`), plain `404`, and `400 invalid-request` for `upload` with `email` or `pushId`.

- [ ] **Step 1: Write failing tests** (the spec §7.6 list, one behaviour each):
  - `Should_CreateVersion1And2_When_TwoCellsEditedAndRowAdded` (version 2 `changes` has exactly 3 ops);
  - `Should_CreateOnlyVersion1_When_NoRowEdits`;
  - `Should_ShowReceivedAndRowEvents_When_UploadSaved`: the audit feed has `received` with
    `actor.kind == "user"` and `detail.source == "upload"`, plus 3 row events;
  - `Should_Return409WithFirstId_When_SavedAgain`;
  - `Should_ReplayCreated_When_SameKeyAndBodyRetried`;
  - `Should_Return410_When_Expired` (fake clock + 24 h);
  - `Should_Return404_When_OtherUsersExtraction`;
  - `Should_Return403_When_ApiKeyClientSendsUpload`;
  - `Should_Return400_When_UploadWithPushId`;
  - `Should_IgnoreClientUploadFields_When_BodyHasFileName`;
  - `Should_ReturnUploadOnDetailAndList_When_Saved`, with `email` null;
  - `Should_FindByFileName_When_QMatches`;
  - `Should_ExcludeUploadFromHashAndChanges_When_Saved`;
  - `Should_SaveAsToday_When_NoUpload` (the existing `V2SaveTests` stay unedited and green).
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The intake builds version 1 with `UploadVersionBuilder.Version1`.
  - It runs `externalId` assignment on the body.
  - It computes the version 2 change list with `PayloadDiff` under `PayloadRules.V2`, and creates version 2 only when
    the `transactions` differ.
  - It computes read, hash, fingerprints, snapshot and duplicates on the current version.
  - It then calls `SaveUploadAsync`.
  - n8n v2 saves with email metadata write `created` detail `{ source: "email" }`, through the existing save path's
    event write.
- [ ] **Step 4: Run** the full `dotnet test`. Expected: green.
- [ ] **Step 5: Commit** `T-1213: v2 save of a reviewed upload (version 1 read, version 2 reviewed)`.

### Task 14 (T-1214): Job pipeline through OpenRouter, end to end (fake)

**Files:**
- Test (new): `tests/QbAutopost.Api.Tests/Jobs/OpenRouterJobTests.cs`

**Interfaces:**
- Consumes: T-1202's settings and client.

- [ ] **Step 1: Write the tests.**
  - `Should_RunPdfJobThroughOpenRouter_When_Configured`: the sample job's PDF goes through the real `HermesClient`
    with a fake handler returning the existing T1/T2/T3 answer fixtures. It asserts:
    - the URL is `https://openrouter.ai/api/v1/chat/completions`;
    - the header is `Authorization: Bearer <marker>`;
    - `model` is `~google/gemini-pro-latest`;
    - `rows.json` / `result.json` equal the same job run through `FakeHermesClient`.
  - `Should_HoldAsToday_When_OpenRouterDisabled`.
  - `Should_ScrubKeyFromAuditCopies_When_JobRuns` (a marker key of at least 8 characters).
- [ ] **Step 2: Run.** Expected: PASS, or fix what fails and note it.
- [ ] **Step 3: Commit** `T-1214: job pipeline end to end through OpenRouter (fake handler)`.

### Task 15 (T-1215): Runbook and server checklist **[server]**

**Files:**
- Create: `docs/testing/T-1215.server-checklist.md`
- Modify: `docs/runbook.md` (new section: OpenRouter key and extraction)

- [ ] **Step 1: Write the runbook section:**
  - setting `OpenRouter__ApiKey` as a machine environment variable for the service account;
  - `OpenRouter__Enabled=true`, `Extraction__Enabled=true`;
  - outbound HTTPS to `openrouter.ai`;
  - stopping and disabling the Hermes container;
  - Docker is no longer required.
- [ ] **Step 2: Write the checklist** (spec §10):
  - migrate;
  - set the key;
  - `GET /api/v2/health` shows `extraction: configured`;
  - upload `D:\sample_payload\152_09_093026.pdf`;
  - run the same PDF through n8n and compare the rows on `date`, `amount`, `payee`, `lineAccount`, `memo`, listing
    the expected differences (§6.10);
  - save, post to the test company file, and check Home and `/dashboard/summary`;
  - run one PDF job through OpenRouter;
  - confirm Docker and Hermes no longer start;
  - confirm the sample PDF was not copied anywhere under `C:\qb-autopost`.
- [ ] **Step 3:** Set the tracker row to `ready-for-human` and stop.
- [ ] **Step 4: Commit** `T-1215: OpenRouter runbook and M12 server checklist [server]`.

---

## Acceptance (from spec §13; tick on completion)

- [ ] E-0 No v1 change (T-1202 … T-1213, `V1ContractPinTests`)
- [ ] E-1 R-10 pinned (T-1204, index in T-1209)
- [ ] E-2 R-11 (T-1205, T-1211, T-1212)
- [ ] E-3 R-12 (T-1211, T-1212)
- [ ] E-4 R-13 (T-1206 … T-1209, T-1212; the n8n comparison in T-1215)
- [ ] E-5 R-14 (T-1208, T-1210, T-1213)
- [ ] E-6 Nothing persisted, no key leaks, no real calls (T-1212, T-1214)
- [ ] E-7 Build and tests green on Windows and Linux, no new package (every task)
- [ ] E-8 Whole app on OpenRouter (T-1202, T-1203, T-1214, T-1215)
