# Spec — Dashboard API M12 (`/api/v2`): Home, posted-time filters and manual PDF upload (R-10 … R-14)

**Date**: 2026-10-28 · **Status**: draft for owner review · **Milestone**: M12 (tasks T-1201 …)
**Source**: `D:\qb_post_react\docs\api-requirements-for-dashboard.md` (updated 2026-10-28), requirements R-10 … R-14,
and the n8n workflow export `Quickbooks Automation.json` (owner, 2026-10-28).
**Builds on**: `docs/saved-requests-dashboard-v2.md` (M11, "v2 spec"), which is assumed as written. `v2 §n` refers to
it; `M10 §n` refers to `docs/saved-requests-postgres.md`.

R-1 … R-9 shipped in M11 under `/api/v2`. This spec covers what the requirements file added afterwards: what the Home
page relies on (R-10), the summary endpoint (R-11), posted-time and time-zone filters (R-12), and the manual PDF upload
(R-13, R-14). Everything is served **only under `/api/v2`**; `/api/v1/**` stays byte-identical, as in M11.

---

## 0. Owner decisions, 2026-10-28

| # | Decision |
|---|---|
| M-1 | **All of R-10 … R-14 in one milestone (M12).** |
| M-2 | **The upload reads PDFs through OpenRouter directly**, with its own API key. Hermes is **not** used for this feature. |
| M-3 | **The extraction prompt is the n8n "Information Extractor" prompt, edited** so that every field the model cannot find is left empty. The defaults in the n8n prompt (`Capital One - 3188`, "Ask My Accountant" for unknowns) are removed. The edited prompt has its own prompt version. |
| M-5 | **The whole app moves from Hermes to OpenRouter** (owner, 2026-10-28). Hermes, the local Docker container on `127.0.0.1:8642`, is no longer used anywhere. The job pipeline's model calls (T1 spec, T2 PDF statements, T3 invoices, T4 judgment) and the upload extraction all go to OpenRouter, through one client and one key. Statement and invoice text therefore leaves the server for OpenRouter; that is the owner's decision. See §14. |
| M-4 | From the requirements file (unchanged here): the PDF is discarded after reading; fields the model did not find stay empty; `controlTotal` is never computed; possible duplicates warn and never block; the user reviews before anything is saved. |

## 1. Anti-goals (each is tested where this work touches it)

1. **No v1 change.** `/api/v1/**`, its OpenAPI document (SHA-256 pinned by `V1ContractPinTests`) and every existing
   test stay unchanged.
2. **No computed control total.** An extraction always returns `controlTotal: null`. The API never sums rows into
   `controlTotal`, never rounds an amount, never "fixes" one. The posted-value figure in R-11 is a report over stored
   amounts, written nowhere.
3. **No made-up values.** No field is ever filled with a default (§6.4).
4. **No PDF and no PDF text persisted** anywhere: database, `output/`, logs, audit copies or temp files left behind.
5. **No secret leaves the server.** The OpenRouter key never appears in a response, a log line, a fixture or an
   exception message.
6. **No test calls OpenRouter, QuickBooks or Hermes.** OpenRouter is faked at the `HttpMessageHandler` level.
7. **No new package, no new background timer.** The `JobWorker` stays the only one; expiry is done in-line (§7.3).
8. **Audit events are never updated or deleted.** Creating or expiring an extraction writes no audit event.

## 2. Shape of the solution

- **Core** (`QbAutopost.Core`): pure rules only:
  - the extraction tidy-up (§6.4);
  - the upload duplicate matcher (§6.7);
  - time-zone bucketing and the percentile helper for the summary (§4);
  - the upload version builder (§7.2).
  - Core gets no EF, ASP.NET or `HttpClient` registration. It may hold the OpenRouter request/response DTOs and the
    reply parser, which are plain `System.Text.Json`.
- **Data** (`QbAutopost.Data`): one migration, `DashboardM12`:
  - the upload columns (§7.1);
  - the `statement_extractions` table (§6.6);
  - the R-10 index (§3.4).
  - New store methods, mirrored in `FakeSavedRequestStore` and covered by one contract suite run against both stores.
- **Api** (`QbAutopost.Api`):
  - new routes under the existing `/api/v2` group;
  - the OpenRouter client (a typed `HttpClient`);
  - `Extraction:*` settings;
  - the per-user upload limiter;
  - the v2 save change;
  - the OpenAPI v2 entries.

## 3. R-10: what the Home page relies on (confirm and pin)

M11's `/api/v2/audit-events` already behaves as R-10 asks, with one difference (3.2). M12 adds tests that pin each
point so it cannot change underneath the dashboard. **No behaviour changes except the index (3.4).**

### 3.1 Pinned by new tests (`V2HomeContractTests`)

1. `total` is the count of **all** matching events whatever `pageSize` is; `pageSize=1` is accepted.
2. A repeated `action` means "any of these" and is AND-ed with `from`/`to` and every other filter.
3. Event meaning:
   - `received`: exactly once per saved request, `atUtc` = the save time (not the email time).
   - `posted`: once per attempt that finished `posted`, `atUtc` = the attempt's `finished_utc`.
   - `post-failed`: once per attempt that finished with any other outcome, `atUtc` = `finished_utc`. `post-started`
     is a separate action and is never one of these.
   - `discarded`: once per discard. Discard → restore → discard gives two.
4. For a caller without `users:manage`, `user-*` events are absent from `items` **and** from `total`.
5. Order is `atUtc desc, id desc` and stable across pages: paging a fixed data set with `pageSize=200` returns every
   event exactly once.
6. `GET /api/v2/saved-requests?status=pending&status=partial&status=unknown&sort=received&pageSize=100` returns oldest
   first, with `received` falling back to `created`.
7. **Acceptance (R-10):** for any range, `total` for `action=X&from&to&pageSize=1` equals the number of rows the same
   filter returns across all pages.

### 3.2 Difference: `to` is exclusive

R-10.2 says `from` and `to` are both inclusive. v2 §8.1 made `to` **exclusive**, and M12 keeps it: back-to-back
periods (`[a, b)` then `[b, c)`) then never count a boundary event twice. This is added to the v2 spec's §14
differences table and to the requirements file's reply (§11). R-11 and R-12 use the same rule: `from` inclusive,
`to` exclusive.

### 3.3 `partial`

`post-failed` covers outcome `partial` (Q-104, unchanged). Home counts it as a failure; `detail.outcome` says
`partial`.

### 3.4 Index

The count queries read the `audit_events` view filtered by time and action. M11 added descending time indexes on
`saved_request_versions(created_utc)`, `saved_request_events(at_utc)` and `user_audit(at_utc)`. M12 adds
`ix_saved_request_events_kind_at` on `saved_request_events(kind, at_utc desc)`, because `received`, `posted`,
`post-failed` and `discarded` all come from that table.

*Builder's call:* if `EXPLAIN` on the fake-free Testcontainers database shows the view's `UNION ALL` defeating the
index, the count path for these four actions may query `saved_request_events` directly. It must stay equal to the view
(pinned by 3.1.7).

## 4. R-11: `GET /api/v2/dashboard/summary`

### 4.1 Request

- Scope `requests:read` (every role).
- Query parameters:
  - `from`, `to` (required): ISO instants with an offset. `from` inclusive, `to` exclusive.
  - `compareFrom`, `compareTo` (optional, both or neither).
  - `bucket`: `day` (default) or `week`. Weeks start on Monday.
  - `tz`: an IANA zone id (default `UTC`), resolved with `TimeZoneInfo.FindSystemTimeZoneById`, which accepts IANA
    ids on Windows and Linux under .NET 8.
- `400 invalid-request` (the v2 problem type for bad query values) when:
  - a value does not parse, or `tz` is unknown;
  - `to <= from`, or `compareTo <= compareFrom`;
  - either range is longer than 366 days;
  - only one of `compareFrom`/`compareTo` is sent.

### 4.2 Response

```json
{
  "range":    { "from": "2026-09-29T00:00:00+05:30", "to": "2026-10-28T09:41:00+05:30", "tz": "Asia/Kolkata", "bucket": "day" },
  "counts":   { "received": 41, "posted": 37, "postFailed": 3, "discarded": 2 },
  "previous": { "received": 35, "posted": 39, "postFailed": 1, "discarded": 2 },
  "posted":   { "rows": 1180, "amount": 248391.27, "previousRows": 1204, "previousAmount": 251002.10 },
  "timeToPost": { "medianMinutes": 1260, "p90Minutes": 4320, "statements": 37 },
  "series": [ { "start": "2026-09-29", "received": 2, "posted": 1, "postFailed": 0 } ]
}
```

- **`range`** echoes the request, rendered in `tz`.
- **`counts` / `previous`** use exactly §3.1.3, so they equal the audit totals for the same filters (the Home figures
  link to the audit trail filtered the same way). No user events are involved; the endpoint needs no
  `users:manage` rule.
- **Time source.** Every time in this endpoint is the time the matching audit event uses (the `post-finished`
  event's `at_utc`, as the `audit_events` view does), never a second column such as `post_attempts.finished_utc`.
  The two are equal for normal endings, and reading one source keeps `counts` equal to the audit feed.
- **`posted.rows` / `posted.amount`** count **money that reached QuickBooks**:
  - They cover attempts that finished `posted` **or `partial`**, with the finish event in the range. *My call:* a
    `partial` attempt did post rows, so leaving it out would under-report money. `counts.posted` still counts only
    `posted` events, so it stays equal to the audit total.
  - `rows` is the number of entries in the attempt's stored result `posted[]` (`DirectPostedRow`, each with a TxnID).
    Rows set aside (`post: false`) and held rows are not in `posted[]`.
  - `amount` is the sum of `posted[].amount` from that same stored result. That is the amount actually sent and
    recorded. There is no join back to the version payload, so there is only one source.
  - It is a `decimal` sum of stored values, with no rounding. Serialised as a JSON number with 2 decimals (`0.00`
    invariant).
  - It is not the sum of control totals, because control totals include skipped rows.
  - A `partial` or `unknown` attempt later **resolved** to posted adds nothing more. Its rows were already counted
    from its stored result, if it had one. An `unknown` attempt has no `posted[]` and adds 0.
  - `previousRows` / `previousAmount` cover the compare range, and are `null` when no compare range is sent.
- **`timeToPost`:**
  - Covers each statement whose **posted time** falls in the range, using the same definition as
    `postSummary.postedUtc` (`PostSummaries.Of`): the latest posted or partial attempt's finish (as `PostSummaries.Of`), or, for a request
    resolved to posted, its `resolved_utc`. *My call:* one definition of "when it was posted" across the summary, the
    list filter (§5) and the list column.
  - For each, it measures minutes from `coalesce(email_received_utc, created_utc)` to that posted time.
  - `medianMinutes` and `p90Minutes` use the **nearest-rank** method on the sorted minutes (integers, rounded down).
    `statements` is how many statements were measured.
  - The whole object is `null` when there are none.
- **`series`:**
  - One entry per bucket that **overlaps** `[from, to)`, in time order, **including empty buckets**.
  - `start` is the bucket's local date in `tz`: `yyyy-MM-dd`, the Monday for weeks.
  - Bucket boundaries are local midnight in `tz`, converted to UTC per bucket, so days around a DST change are 23 or
    25 hours long. Counts follow §3.1.3.
  - The first and last buckets are clipped to the range, so `series` always sums to `counts`.
- **No per-person figures** (R-11.3). Nothing here names a user.

### 4.3 Acceptance (R-11)

- For the same range, `counts` equals the four audit `total`s.
- `series` sums to `counts`.
- `posted.amount` equals the sum of `posted[].amount` over the posted and partial attempts that finished in the
  range.
- Day buckets follow `tz`: an event at 2026-10-27T20:00Z lands on `2026-10-28` for `Asia/Kolkata` and on `2026-10-27`
  for `UTC`.
- A DST zone (`America/New_York` across 2026-11-01) gives one bucket per local day.
- The endpoint is in the v2 OpenAPI document.

## 5. R-12: posted-time filters and time-zone-aware dates on `GET /api/v2/saved-requests`

1. **New parameters** `postedFrom` / `postedTo`. They filter on exactly the value the list shows as
   `postSummary.postedUtc` (`PostSummaries.Of`): the posted or partial attempt's finish, or `resolved_utc` for a
   request resolved to posted. A request with no `postSummary` never matches when either is sent. The SQL and the fake
   store copy that one definition, and a contract test pins it against `PostSummaries.Of`.
2. **New sorts** `sort=posted` / `-posted`. Requests without a post sort **last** in both directions, then by `id desc`.
3. **New parameter** `tz` (IANA zone id, optional). It applies to the date-only parameters `receivedFrom`,
   `receivedTo`, `postedFrom`, `postedTo`, `createdFrom` and `createdTo`:
   - **with `tz`**, a plain date means that **local** day in `tz`;
   - **without `tz`**, a plain date keeps its current meaning (a UTC calendar day). Nothing changes for existing
     callers.
   - In both cases, `xFrom=d` covers from the start of day `d`, and `xTo=d` covers up to the **end** of day `d`
     (inclusive dates), as the existing date filters do today.
4. **UTC instants are also accepted** in those six parameters. An ISO instant with an offset is used as is, with
   `From` inclusive and `To` exclusive, and `tz` is ignored for that parameter.
5. Bad `tz` or bad date → `400 invalid-request`.

**Acceptance (R-12):** `postedFrom=2026-10-27&postedTo=2026-10-27&tz=Asia/Kolkata` returns exactly the statements
whose post finished on that local day (2026-10-26T18:30Z ≤ t < 2026-10-27T18:30Z).

## 6. R-13: `POST /api/v2/statement-extractions`

### 6.1 Request and limits

- `multipart/form-data` with exactly one file part named `file`. Any other part is ignored.
- Scope **`requests:submit`** (editor and admin). Viewers get `403`, and API-key clients get `403`, because an
  extraction belongs to a signed-in **user** (§6.6).
- Checks, in order. Nothing is stored and OpenRouter is not called when any of them fails:

| Check | Problem |
|---|---|
| Extraction not configured (`Extraction:Enabled` false, or no key) | `503 extraction-unavailable` |
| No `file` part | `400 invalid-request` |
| Body over `Extraction:MaxBytes` (default 10 MB = 10 485 760) | `413 file-too-large`, detail names the byte limit. See "Body size" below. |
| File does not start with `%PDF-` | `415 not-a-pdf` |
| PdfPig cannot open it | `415 not-a-pdf` |
| PdfPig reports it encrypted / password-protected | `422 pdf-protected` |
| More than `Extraction:MaxPages` (default 50) | `413 file-too-large`, detail names the page limit |
| Every page under `PdfText.ScannedPageThreshold` (40 non-space characters) | `422 no-text`: "This PDF has no readable text. It may be a scan." |
| Same user already has an extraction in flight | `429 too-many-extractions`, `Retry-After: 30` |
| More than `Extraction:MaxPerUserPerHour` (default 20) started by the user in the last hour | `429 too-many-extractions`, `Retry-After` = seconds until the oldest of those leaves the window |

- **Body size.** Today one global limit applies to every request: `Api:MaxRequestBodyBytes` (2 MB), enforced by
  `RequestSizeMiddleware` and by Kestrel (`Program.cs`). M12 must:
  - raise Kestrel's server limit to the larger of the two settings;
  - make `RequestSizeMiddleware` use `Extraction:MaxBytes` (plus a small multipart allowance, *builder's call*) **only
    for this route**, and answer with `413 file-too-large` there;
  - keep every other route at 2 MB with today's problem, unchanged (pinned by a test).
  The body is never buffered beyond the limit.
- **Scanned PDFs** are not sent to the model in M12. Sending the PDF itself as a file input is a later option
  (Q-105).
- **Limiter.** The rate limiter is in-memory and per user. A restart resetting it is acceptable. It sits alongside
  the existing `RateLimitPolicy`, which still applies.
- **Working copy.** The file is read into memory (at most 10 MB) or into a temp file that is deleted in a `finally`.
  No copy survives the request.

### 6.2 Reading

- **Text.** PdfPig text per page, joined with `PdfText`'s page break.
  - Today `PdfText.ReadAsync` takes only a path, holds the **whole** file when any one page is short, and turns every
    exception into one `Held` result. The upload needs different outcomes, so M12 adds a new reader in Core
    (`UploadPdfReader`, or a new `PdfText` method; *builder's call*) that:
    - reads from a byte array or stream;
    - reports encrypted (→ 422), unopenable (→ 415), page count, and per-page text, as distinct results;
    - treats a short page as empty text and returns `no-text` only when **every** page is short;
    - never calls OCR.
  - The existing `PdfText.ReadAsync` behaviour for jobs is unchanged.
  - n8n uses a different PDF library, so its text is not byte-identical. That is accepted; the acceptance test does
    not depend on it (§6.10).
- **The call** goes through the app's one OpenRouter chat client (§14), the same one the job pipeline uses:
  - `POST {OpenRouter:BaseUrl}/chat/completions`, with header `Authorization: Bearer {OpenRouter:ApiKey}`.
  - `model` = `Extraction:Model`, default `~google/gemini-pro-latest`, exactly as in the n8n workflow (the `~` is
    OpenRouter's "latest" alias; the requirements file dropped it).
  - `temperature: 0`.
  - `messages`: the system prompt (§6.3), then one user message holding the PDF text.
  - `response_format: { "type": "json_schema", "json_schema": { "name": "bank_statement", "strict": true,
    "schema": <§6.3 schema> } }`.
- **Optional OpenRouter headers.** *Builder's call:* send `HTTP-Referer` and `X-Title` only if configured. They carry
  no secret.
- **Reply check:**
  - The reply content (code fences stripped, as `JsonReply` does for Hermes) must parse and match the schema.
  - If it does not, retry **once**, with the validation errors appended to the user message, as `HermesClient` does.
  - If the retry also fails → `502 extraction-failed`.
  - An HTTP error from OpenRouter (any non-2xx, or a network error) → `502 extraction-failed`, **no** retry.
- **Timeout.** No answer within `Extraction:TimeoutSeconds` (default 120) → `504 extraction-timeout`.
- **Cancellation.** A client disconnect cancels the OpenRouter call. Nothing has been written, so this is safe.
- **No audit copies.** Job calls write prompt/reply copies into the job folder (FR-17), and still do. Upload calls
  write **none**, because the copies would contain the PDF text (anti-goal 4). The shared client already skips them
  when `HermesRequest.AuditDir` is null. The extraction path always passes null, and a test proves no file is written.
- **Request shape.** The shared client gains optional `response_format` (JSON schema) support. Job calls keep their
  current request shape unless §14 says otherwise.
- **Logging.** One line per extraction with `userId`, file size, page count, model, prompt version, duration, row
  count and outcome. Never the file name's bytes beyond the sanitised name (§6.8), never text, never the key.

### 6.3 Prompt, schema and version (`Extraction:PromptVersion` = `2026-10-28.api-1`)

The prompt, schema and model live together in **one file** in the Api project:
`src/QbAutopost.Api/Extraction/extraction-prompt.json`, holding `promptVersion`, `systemPrompt` and `schema`, embedded
as a resource. The model and the switch stay in configuration. A test pins the file's SHA-256 together with its
`promptVersion`, so editing the prompt without bumping the version fails the build.

**Schema:** the n8n node's `inputSchema`, copied verbatim, with these edits (M-3 applies to the descriptions too,
because the model reads them as instructions):
- `transactions.items.required` becomes only `["kind", "amount"]`, instead of n8n's eight required fields, so the
  model may omit any field it cannot find.
- `last4` and `checkNo` accept `"type": ["string", "null"]`.
- `reference` description: `"A statement reference identifier printed on the statement, if any. Omit if none."`
  This drops the `'zemen-2019-12-p01'` example.
- `account` description: `"Primary bank account name and last 4 digits from the statement header, e.g. '<Bank> -
  <last4>'. Omit if not shown."` This drops the real account example.
- `lineAccount` description: n8n's text with `'Ask My Accountant'` removed from the example list, plus `"Omit if
  unclear."`
Everything else, including the other descriptions and `additionalProperties: false`, is n8n's text.

> *Builder's note:* if OpenRouter's strict mode for the configured model rejects optional properties, fall back to
> `"strict": false` with the same schema. The reply is still validated in code. Record which mode was used in the
> tracker.

**System prompt:** n8n's text, with rules 5 and 6 and the opening "omit" line edited (shown in full; the edits are
marked ★ here and are **not** part of the stored text):

```
You are an expert extraction algorithm.
Only extract relevant information from the text.
If you do not know the value of an attribute asked to extract, omit the attribute. Never guess or use a placeholder value. ★

CRITICAL RULES FOR EXTRACTION:
1. 'date': Convert all dates to standard 'YYYY-MM-DD' format (e.g., '12/02' becomes '2019-12-02').
2. 'amount': Always output amounts as POSITIVE numbers (absolute values).
3. 'kind': MUST ONLY BE 'Deposit' OR 'Check'. Set 'Deposit' for credits/deposits (positive amounts) and 'Check' for ALL withdrawals, debit card purchases, ACH fees, transfers out, service charges, or checks.
4. 'externalId': Generate sequential row IDs starting from "row-1", "row-2", "row-3", etc.
5. 'account': Use the bank name and last 4 digits of the account found in the statement header (e.g., "Capital One - 3188"). If the header does not show them, omit 'account'. ★
6. 'lineAccount': Categorize based on description context:
   - "Merchant Card Deposits" for BANKCARD SETTLEMENT
   - "Bank Service Charges" for fees or monthly charges
   - "Supplies & Materials" or "Inventory Asset" for purchases (Costco, LA Mart, Aldi)
   - If the category is not clear from the description, omit 'lineAccount'. ★
7. 'payee': Extract clean vendor or counterparty names:
   - "BANKCARD SETTLEMENT" -> "Bankcard Merchant Services"
   - "BANKCARD MTHLY FEES" / "Service charge" -> "Bank Fees"
   - "TRANSFER DEPOSIT" -> "Account Transfer In"
   - Vendor names -> "Costco Wholesale", "LA Mart", "RJ Reynolds Tobacco", etc.
   - If no counterparty can be identified, omit 'payee'. ★
8. 'checkNo': Extract numeric check number if present, otherwise null.
9. 'last4': Extract card or account last 4 digits if present in text, otherwise null.
```

The other changes from n8n's text:
- the duplicated rule number "1." for `kind` is renumbered "3.";
- the "Ask My Accountant for transfers or checks to unknown vendors" bullet is removed (it is a default by another
  name).

The n8n copy is not changed by this work.

### 6.4 Tidy-up (`ExtractionTidy` in Core, pure)

From the model's parsed reply to the response `payload`. These are n8n's Code-node rules **minus its defaults and
minus its control-total sum**:

| Field | Rule |
|---|---|
| `kind` | `Deposit` when the model's `kind` equals `deposit` (case-insensitive), otherwise `Check`. **The n8n rule `|| item.amount > 0` is not copied**: amounts are always positive, so in n8n it makes every row a `Deposit`. |
| `amount` | The model's number, made positive if negative (absolute value), with no other change. If it is not a JSON number, the field is **omitted** (the row stays, so the user sees it and fills it in). |
| `externalId` | `row-1`, `row-2`, … by position in the reply. The model's own `externalId` is ignored. |
| `date`, `account`, `lineAccount`, `memo`, `last4`, `payee`, `checkNo` | Copied when the model returned a non-empty string (after trimming), otherwise **omitted**. `date` is copied even if it is not `yyyy-MM-dd`; the existing save-time read errors flag it. |
| `refNumber` | Never produced (not in the schema). |
| `reference` | The model's trimmed `reference`, or **null**. |
| `controlTotal` | **Always null.** |

Money handling: `amount` is read from JSON as `decimal` (`JsonElement.GetDecimal`), never through `double`.

**This complies with rule 3** ("the model never computes money"): the model only *transcribes* amounts printed on the
statement. Nothing is posted from an extraction. The user types the statement's control total, and the existing
validate-and-post control-total check must reconcile the rows to it before anything posts.

### 6.5 Not a bank statement

- The model returns `is_bank_statement: false` → `422 not-a-bank-statement` ("This PDF doesn't look like a bank or
  card statement.") and **no** extraction record.
- `is_bank_statement: true` with zero rows → `200` with `transactions: []`.

### 6.6 Extraction record: table `statement_extractions`

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` pk | `extractionId`, random v4 |
| `user_id` | `text` not null | The uploading user's id |
| `file_name` | `text` not null | Sanitised (§6.8) |
| `size_bytes` | `bigint` not null | |
| `sha256` | `text` not null | Lower hex of the uploaded bytes |
| `page_count` | `int` not null | |
| `model` | `text` not null | The model **as configured** |
| `prompt_version` | `text` not null | |
| `payload` | `jsonb` not null | The tidied payload (§6.4) |
| `extracted_utc` | `timestamptz` not null | |
| `expires_utc` | `timestamptz` not null | `extracted_utc` + `Extraction:RecordHours` (default 24) |
| `saved_request_id` | `bigint` null, FK `saved_requests(id)` | Set at save (§7) |

- Index on `expires_utc` where `saved_request_id is null`. The app role gets `SELECT, INSERT, UPDATE, DELETE` on this
  table only. `deploy/migrations.sql` and `deploy/grants.sql` are regenerated, and the grants drift test covers it.
- **Saved records are kept** for as long as the statement exists, because `same-file` (§6.7) and `409` (§7.3) need
  them. Only **unsaved, expired** records are deleted.
- **No audit event** is written for creating, saving-into or deleting a record (the statement's own `received` event
  covers the save).

### 6.7 Possible duplicates (`UploadDuplicateMatcher` in Core, pure; warning only)

Compared against saved requests of any status created within `SavedRequests:DuplicateLookbackDays` (90 today, the
same window the save-time detector uses, `SavedRequestIntake.DetectAsync`). *My call:* use that window rather than
"every saved request", so both checks see the same history. `same-file` ignores the window, because it is an exact
index lookup on `upload_sha256`. At most 5 are returned, newest `created_utc` first. Each carries every reason that
applies:

- **`same-file`:** the upload's SHA-256 equals `upload_sha256` of a saved request (§7.1). Unsaved extractions do not
  count.
- **`same-reference`:** the extracted `reference` is non-empty and equals a saved request's **current** `reference`.
  The comparison is case-insensitive, as `DuplicateDetector` does today. R-13 says "exact", and case-insensitive
  matches more, so it warns more, which is the safer side.
- **`overlapping-rows`**, either of:
  - (a) at least one extracted row's §9 row fingerprint equals one of the saved request's stored row fingerprints;
  - (b) at least **half** of the extracted rows (rounded up, and at least 1) match rows of that **same** saved request
    on `(kind, date, amount)` alone. This catches uploads whose rows lack `account` or `payee`.

`source` is `email` when the saved request has email metadata, `upload` when it has upload metadata, otherwise `other`.

Response item: `{ id, publicRef, reference, status, receivedUtc, source, reasons[] }`. `receivedUtc` =
`coalesce(email_received_utc, created_utc)`.

Stored row fingerprints do not contain `(kind, date, amount)` in readable form, so rule (b) needs its own data.
*Builder's call:* either a new store query that reads `kind, date, amount` from candidates' current payloads (jsonb)
in the window, or a stored key set computed at save. Whichever is chosen, the fake store mirrors it and the contract
suite covers it.

### 6.8 Response `200`

R-13.7's shape, unchanged. `model` is the configured model string.

`file.name` is the client's file name made safe:
- path parts stripped;
- control characters (U+0000–U+001F, U+007F–U+009F) removed;
- trimmed, cut to 255 characters without splitting a surrogate pair;
- `upload.pdf` if nothing is left.

It is stored and returned as text, never used as a path.

### 6.9 Problems

Every failure is RFC 7807 with the v2 problem `type` URIs: `extraction-unavailable`, `file-too-large`, `not-a-pdf`,
`pdf-protected`, `no-text`, `not-a-bank-statement`, `extraction-failed`, `extraction-timeout`,
`too-many-extractions`. All are in the v2 OpenAPI document.

### 6.10 Acceptance (R-13)

- **Pipeline (automated).** There is no recorded n8n output (owner, 2026-10-28), so the automated tests check the
  API's own pipeline, not the model's reading:
  - The repo's synthetic `tests/fixtures/statements/chase-checking-4521.pdf` is uploaded.
  - It is paired with a hand-written OpenRouter reply fixture (`tests/fixtures/extractions/chase-checking-4521.reply.json`)
    served by the fake handler. The fixture includes rows with missing fields, a negative amount, a non-number amount
    and the model's own `externalId`s.
  - The test asserts the §6.4 tidy-up exactly: blanks stay blank, no default appears, `kind` follows the model,
    `externalId` is renumbered, and `controlTotal` is null.
  - It also asserts that the request sent to the fake handler carries the PDF's text, the prompt file's system prompt
    and schema, and the configured model.
- **Same rows as n8n ([server], by a person).** The owner's sample `D:\sample_payload\152_09_093026.pdf`, a 4-page
  statement with a text layer on every page, is run through both n8n and the upload on the server, and the rows are
  compared on `date`, `amount`, `payee`, `lineAccount` and `memo`. Expected differences:
  - blanks where n8n wrote a default;
  - `kind` from the model, not n8n's always-Deposit;
  - `controlTotal` null;
  - small wording differences caused by the different PDF text extraction.
  The result is recorded in the server checklist.
  - **The PDF is real client data and is never copied into the repo**, fixtures or `output/`. *My call:* the
    automated tests use only the synthetic fixture above.
- **Not a statement.** A non-statement reply gives `422 not-a-bank-statement` and no record.
- **Nothing kept.** After any call, success or failure, there is no temp file, no audit-copy file, and no row or log
  line containing the PDF text. A test feeds a PDF with a unique marker string and searches the database, `output/`
  and the captured logs for it.
- **Duplicates.** A second upload of a saved file lists the first statement with `same-file`.
- **Problems.** Every row of the §6.1 table, and the 502 / 504 cases, have a test.
- **Key never leaks.** No response or log line contains the configured key (test with a marker key).

## 7. R-14: saving a reviewed upload (`POST /api/v2/saved-requests`)

### 7.1 New columns on `saved_requests`

All are nullable and filled **only from the extraction record at save, never from the client**. They are never
changed afterwards:
- `upload_extraction_id` (`uuid`)
- `upload_file_name` (`text`)
- `upload_size_bytes` (`bigint`)
- `upload_sha256` (`text`, indexed)
- `upload_model` (`text`)
- `upload_prompt_version` (`text`)
- `upload_extracted_utc` (`timestamptz`)

### 7.2 Body and versions

The v2 save body may carry `upload: { "extractionId": "<uuid>" }`. Like `email` and `pushId`, `upload` is metadata:
excluded from binding, from the v2 canonical hash and from the change list (`PayloadRules.V2` gains `upload` in its
metadata set).

When `upload` is present:

1. **Checks** run under the same transaction as the insert, with the extraction row locked (`FOR UPDATE`):
   - **Unknown id, or `expires_utc` passed:** `410 extraction-expired` ("Upload the PDF again."). An expired unsaved
     record is deleted in the same step.
   - **Another user's:** `404` (the plain v2 not-found problem, revealing nothing).
   - **Already saved:** `409 extraction-already-saved` with `savedRequestId` and `publicRef`. Nothing new is written.
     A double click or a retry after a lost response lands here.
   - **Caller is an API-key client:** `403`.
   - **`email` or `pushId` also present:** `400 invalid-request`. An upload has neither.
2. **Version 1** = the extraction record's `payload.transactions` (what the model read), with the **body's**
   `reference` and `controlTotal`:
   - `controlTotal` is the body value exactly as sent; the dashboard sends `null` when the user left it empty.
   - Created by the uploading user, note `Read from {file_name}`.
   - Built by `UploadVersionBuilder` in Core (pure).
3. **Version 2** = the body payload, created only if its `transactions` differ from version 1's under the v2 change
   list (§7 of the v2 spec, by `externalId`):
   - same actor, same time, note `Reviewed before saving`;
   - its `changes` are the ordinary change list, so the audit feed shows `row-edited`, `row-added`, `row-deleted`,
     `row-post-off`, … with no new audit code.
   - A body whose transactions equal version 1's creates only version 1.
4. **`original_body`** = the request body as received, byte for byte (unchanged rule).
5. **`externalId` assignment** (v2 §7.1) runs on the body as for any v2 save. Rows added in the dashboard carry
   `dash-<uuid>` and keep it.
6. **The rest is unchanged.** Read errors, the v2 validation snapshot, duplicate detection (M10 §9), counts and
   `needsAttention` run on the **current** version, as for any save. This also catches a reference the user typed
   during review that matches another statement (R-14.7).
7. The extraction row's `saved_request_id` is set, and the upload columns are copied, in the same transaction.
8. **Response** is the normal v2 save response (`201`).
   - **Order of checks:** a matching `Idempotency-Key` replay (same key, same body) is answered first, exactly as for
     any v2 save: the stored `201` with `Idempotency-Replayed: true`. *My call:* a retry of the same request gets the
     same answer, which is what idempotency promises.
   - Any other save of an already-saved `extractionId` (no key, a different key, or a different body) gets
     `409 extraction-already-saved`.

When `upload` is absent, the save is exactly today's v2 save.

### 7.3 Expiry without a timer

- Every successful `POST /statement-extractions` first deletes unsaved records whose `expires_utc` has passed (one
  `DELETE`, bounded by the §6.6 index).
- A save that finds its record expired deletes it (7.2.1).
- No timer or hosted service is added. An expired record still in the table never matches anything: `same-file` uses
  only saved requests, and a save checks `expires_utc`.

### 7.4 Returned fields

- `upload` on v2 detail **and** list rows: `{ fileName, sizeBytes, sha256, model, promptVersion, extractedUtc }`, or
  `null`.
- `email` is `null` for uploads.
- `source` is the user, as today for a save by a signed-in user.

### 7.5 Audit (R-14.5) and list (R-14.6)

- **`received` event `detail`:**
  - `{ "source": "upload", "fileName": "…" }` for uploads;
  - `{ "source": "email" }` for v2 saves that carry email metadata;
  - `null` for every other save and for older rows. *My call:* "email" is claimed only when email metadata is
    present, so the API never asserts a source it cannot see.
- Still exactly one `received` per saved request (§3.1.3).
- Version 2's changes appear as row events at the same time as `received`. They sort after it under
  `atUtc desc, id desc` only if their ids order so; the dashboard does not depend on the order within one instant.
- **Implementation, which is new store work:**
  - Today the `created` event's detail is always null (`EfSavedRequestStore.cs`), and a save writes only version 1,
    with `changes = []`.
  - M12 adds a store save path that, in **one transaction**:
    - locks the extraction row;
    - writes version 1 and, when needed, version 2 with its `changes`;
    - writes the `created` event with this detail;
    - copies the upload columns;
    - sets `saved_request_id`.
  - The `audit_events` view passes the event detail through (`ELSE e.detail`), so the SQL view needs no change.
  - The Core `AuditEventMapper` used by the fake store must pass it through the same way. A contract test runs
    against both stores.
- **List `q`** also matches `upload_file_name` (case-insensitive contains). `sort=received` uses `created_utc` for
  uploads (no email time), as today.

### 7.6 Acceptance (R-14)

- **Edited review.** Saving an extraction with two edited cells and one added row creates:
  - version 1 equal to the extraction rows, plus the saved reference and control total;
  - version 2 holding exactly those three changes.
- **Unedited review.** Saving with no row edits creates only version 1.
- **Audit.** The feed shows `received` (actor = the user, `detail.source = upload`) and three row events.
- **Second save.** Saving the same `extractionId` again returns `409` with the first statement's id and writes nothing.
- **Expiry.** After 24 hours (fake clock) an unsaved extraction returns `410`.
- **Other user.** Another user's extraction returns `404`.
- **No client metadata.** `upload` columns come from the record, never from the body: a body with extra `upload`
  fields such as `fileName` is ignored.

## 8. Configuration (`Extraction` section; the connection and key are in `OpenRouter`, §14.2)

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Off until the server is configured. It also needs `OpenRouter:Enabled` and a key; otherwise `503 extraction-unavailable`. |
| `Model` | `~google/gemini-pro-latest` | The model for uploads. It may differ from `OpenRouter:Model`, the model for jobs. |
| `TimeoutSeconds` | `120` | |
| `MaxBytes` | `10485760` | |
| `MaxPages` | `50` | |
| `MaxPerUserPerHour` | `20` | |
| `RecordHours` | `24` | |

`PromptVersion` is **not** configuration: it lives in the prompt file (§6.3), so the version and the text cannot
drift apart.

**Health.** `/api/v2/health` gains an `extraction` entry: `disabled`, `configured`, or `misconfigured` (enabled with
no key). It **never calls OpenRouter**, so a health check costs nothing and leaks nothing.

## 14. Moving the whole app from Hermes to OpenRouter (M-5)

### 14.1 What changes and what does not

- **One client.** The existing OpenAI-compatible chat client (`HermesClient`) is the app's one model client, and it
  now points at OpenRouter.
  - It already sends `temperature: 0` and a bearer key, strips code fences and retries once with the validation
    errors, so the job pipeline's behaviour (T1–T4, G1–G3, holds) is unchanged.
  - *My call:* code type names stay as they are (`IHermesClient`, `HermesClient`, `FakeHermesClient`,
    `HermesTask`). That avoids touching about 90 files for a rename that changes no behaviour. Each type's doc
    comment says it is the OpenRouter client.
  - A rename is recorded as a later clean-up (Q-109).
- **No v1 change (anti-goal 1).** These keep their names and shapes, and now report the OpenRouter connection:
  - `GET /api/v1/health/hermes`;
  - the v1 health `hermes` field;
  - the legacy routes.
  The v1 OpenAPI hash stays pinned. v2 health adds a neutral `model` entry (`disabled` / `enabled` plus the job
  model), next to `extraction`.
- **Ping.** `GET …/health/hermes` keeps its one tiny completion (5 tokens). On OpenRouter that costs a fraction of a
  cent per call. It stays behind the `health:read` scope, so monitors that only use `/health` and `/health/ready`
  never trigger it.
- **Rule 3 is unchanged.** The model reads and judges; amounts still come from parsed files or G1-checked T2 rows.

### 14.2 Configuration: `OpenRouter` replaces `Hermes`

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | When false, every model call is held as "model unavailable", exactly as `Hermes:Enabled=false` does today |
| `BaseUrl` | `https://openrouter.ai/api/v1` | Must be `https://` unless the host is loopback. Otherwise startup fails, so the key is never sent in clear text. |
| `ApiKey` | (none) | Environment / secret store only (`OpenRouter__ApiKey`). Never in `appsettings*.json`, logs, `output/`, audit copies or fixtures. `SecretWarnings` and the log scrubber (`LoggingSetup`) cover it, as they covered `Hermes:ApiKey`. `HermesAudit` keeps scrubbing it from audit copies. |
| `Model` | `~google/gemini-pro-latest` | The model for job calls (T1–T4). *My call:* the same model as uploads until the owner chooses otherwise. |
| `TimeoutSeconds` | `120` | Per call, as today |
| `Referer`, `Title` | (none) | Optional OpenRouter attribution headers, sent only if set |

- The `Hermes` section is **removed** from `appsettings.json`.
- If a `Hermes` section is still present at startup, for example in an old server override file, the app logs one
  warning saying it is ignored and names `OpenRouter`. It never reads it.
- *My call:* no automatic carry-over. The old values pointed at a container that no longer exists.

### 14.3 Docs, deploy and project rules

- **Docs:**
  - `docs/spec.md`: the glossary, §4 hosting and FR-2/3/5 say "the model (OpenRouter)" instead of Hermes. FR
    behaviour is unchanged.
  - New `docs/adr/0011-openrouter-for-model-calls.md` supersedes ADR-0005 for the endpoint (the OpenAI-compatible
    JSON contract of ADR-0005 still holds).
  - `CLAUDE.md` rule 1 and the opening line name OpenRouter; `FakeHermesClient` stays the fake.
- **Deploy:**
  - `deploy/start-all.ps1` and `deploy/install-task.ps1` stop starting Docker and the Hermes container. The API
    start is unchanged.
  - `deploy/hermes/` stays in the repo, marked obsolete in its README, and is removed later (Q-109).
- **Runbook:**
  - set `OpenRouter__ApiKey` for the service account;
  - allow outbound HTTPS to `openrouter.ai`;
  - stop and disable the Hermes container;
  - Docker is no longer needed on the server.
- **Tests:** existing tests keep using `FakeHermesClient` unedited. New tests cover:
  - the `OpenRouter` settings binding and validation;
  - the https rule;
  - the ignored-`Hermes`-section warning;
  - key scrubbing for the new key name;
  - `response_format` passing through the client (fake `HttpMessageHandler`).

### 14.4 Acceptance

- With `OpenRouter` configured (fake handler), a PDF job runs T1/T2/T3 through `https://…/chat/completions` with the
  bearer key and the configured model, and its outputs equal today's for the same fake replies.
- With `OpenRouter:Enabled=false`, the same job holds exactly as it does today with Hermes disabled.
- The v1 OpenAPI hash and every existing test are unchanged.
- No `appsettings*.json` contains a key, and a marker key never appears in logs, `output/` or audit copies.
- `deploy/start-all.ps1` no longer references Docker; the deploy-script tests are updated with it.

## 9. OpenAPI (v2 document)

Documents:
- `GET /dashboard/summary`;
- the R-12 parameters;
- `POST /statement-extractions` (the multipart body, the `200` shape, and every problem `type`);
- the `upload` save field and the `upload` response object;
- the `received` detail.

`OpenApiDriftTests` keeps v2 covering exactly the `/api/v2` routes. The v1 document hash stays pinned.

## 10. Runbook and server work **[server]**

- `docs/runbook.md` gains a section on:
  - setting `Extraction__ApiKey` as a machine environment variable for the service account;
  - `Extraction__Enabled=true`;
  - allowing outbound HTTPS to `openrouter.ai`;
  - the one real end-to-end upload check.
- `docs/testing/T-12xx.server-checklist.md`:
  - migrate the database;
  - set the key;
  - upload `D:\sample_payload\152_09_093026.pdf`;
  - run the same PDF through n8n and compare the rows (§6.10);
  - run one PDF job through OpenRouter (§14.4);
  - confirm Docker and Hermes no longer start;
  - save it, post it to the test company file, and check Home and `/dashboard/summary`.
- A person runs this. The task ends `ready-for-human`.

## 11. Differences from `api-requirements-for-dashboard.md` (the dashboard follows these)

| Req | The document says | The API does | Why |
|---|---|---|---|
| all | unversioned paths | `/api/v2/...` | M11 O-1 |
| R-10.2, R-11, R-12 | `to` inclusive | `to` **exclusive** for instants; date-only `To` stays inclusive of the whole day | No double count at period boundaries (§3.2) |
| R-11.1 | `400 invalid-query` | `400 invalid-request` (the v2 problem type) | Consistency with v2 |
| R-11.2 | — | `range` also echoes `tz` and `bucket`; a range must not be empty | Addition |
| R-13.2 | `415` by content | Also `415` when PdfPig cannot open the file | Same meaning |
| R-13.3 | Scan → send PDF to the model, or `422 no-text` | `422 no-text` | Simplest safe path; Q-105 |
| R-13.5 | `Extraction:PromptVersion` config, model `google/gemini-pro-latest` | Prompt version lives in the prompt file; model `~google/gemini-pro-latest` (as in the n8n workflow) | Version and text cannot drift (§6.3) |
| R-13.5 | Prompt copied verbatim | Edited: no defaults in the prompt (M-3) | Owner decision |
| R-13.4 | `kind`: Deposit when the model says so | Same, and n8n's `amount > 0` rule is deliberately not copied | n8n bug (§6.4) |
| R-13.9 | `same-reference` exact | Case-insensitive | Matches `DuplicateDetector`; warns more |
| R-13.1 | Scope `requests:submit` | Also requires a **user**; API-key clients get `403` | An extraction belongs to a user |
| R-14.2 | — | `email` or `pushId` with `upload` → `400` | An upload has neither |
| R-14.5 | `detail.source = email` for n8n saves | Only when email metadata is present, else `null` | Never claim what can't be seen |

## 12. Questions (for `docs/tracker.md` › Questions)

| # | Question | Choice until answered |
|---|---|---|
| Q-105 | Should scanned PDFs (no text layer) be sent to the model as a file input? | No; `422 no-text` |
| Q-106 | Should n8n switch to `POST /statement-extractions` later, so there is one prompt copy? | Out of scope; two copies, told apart by `promptVersion` |
| Q-107 | Keep `to` exclusive (differs from R-10.2)? | Exclusive |
| Q-108 | Does OpenRouter's strict JSON-schema mode work for the configured model with optional properties? | Try strict; fall back to non-strict with validation in code (§6.3) |
| Q-109 | Rename the `Hermes*` code types to neutral names and delete `deploy/hermes/`? | Later clean-up; names kept in M12 (§14.1) |
| Q-110 | Which OpenRouter model for job calls (T1–T4)? | `~google/gemini-pro-latest`, same as uploads |

## 13. Acceptance criteria

| # | Criterion | From |
|---|---|---|
| E-0 | No v1 change: v1 OpenAPI hash unchanged, every existing test passes unedited (except `OpenApiDriftTests` scope, as in M11). | §1 |
| E-1 | Every §3.1 point pinned by a test against both stores. The R-10 acceptance holds. The new index exists. | R-10 |
| E-2 | §4.3 holds. | R-11 |
| E-3 | §5 acceptance holds; existing date filters without `tz` are unchanged (existing tests unedited). | R-12 |
| E-4 | §6.10 holds. | R-13 |
| E-5 | §7.6 holds. | R-14 |
| E-6 | No test calls OpenRouter; no PDF bytes or text and no key persist anywhere (§6.10). | §1 |
| E-8 | §14.4 holds: the whole app uses OpenRouter; Hermes is used nowhere. | M-5 |
| E-7 | `dotnet build -warnaserror` and `dotnet test` green on Windows and Linux; Data tests skip cleanly without Docker; no new package. | — |

## Appendix A. The extraction schema, as stored in `extraction-prompt.json`

This is the n8n Information Extractor `inputSchema` with exactly the §6.3 edits applied. It is the text the builder
stores; nothing else is copied from the workflow export.

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "properties": {
    "is_bank_statement": {
      "type": "boolean",
      "description": "Return true if this is a bank statement, otherwise false."
    },
    "reference": {
      "type": "string",
      "description": "A statement reference identifier printed on the statement, if any. Omit if none."
    },
    "transactions": {
      "type": "array",
      "description": "List of transactions. Leave empty if not a bank statement.",
      "items": {
        "type": "object",
        "properties": {
          "externalId": { "type": "string", "description": "Unique row identifier starting with 'row-1', 'row-2', etc." },
          "kind": {
            "type": "string",
            "enum": ["Deposit", "Check"],
            "description": "MUST BE EITHER 'Deposit' or 'Check'. Set 'Deposit' for credits/deposits and 'Check' for ALL other outgoing transactions."
          },
          "date": { "type": "string", "description": "Transaction date formatted as YYYY-MM-DD." },
          "amount": { "type": "number", "description": "Absolute positive transaction amount (always a positive number)." },
          "account": {
            "type": "string",
            "description": "Primary bank account name and last 4 digits from the statement header, e.g. '<Bank> - <last4>'. Omit if not shown."
          },
          "lineAccount": {
            "type": "string",
            "description": "Accounting category inferred from transaction detail (e.g., 'Merchant Card Deposits', 'Bank Service Charges', 'Supplies & Materials', 'Inventory Asset'). Omit if unclear."
          },
          "memo": { "type": "string", "description": "Full original transaction details/description line." },
          "last4": { "type": ["string", "null"], "description": "4-digit card or account number mentioned in description, if any." },
          "payee": {
            "type": "string",
            "description": "Entity/merchant/counterparty name (e.g., 'Bankcard Merchant Services', 'Costco Wholesale', 'Bank Fees', 'Account Transfer In')."
          },
          "checkNo": { "type": ["string", "null"], "description": "Check number if the transaction is a check, otherwise null." }
        },
        "required": ["kind", "amount"],
        "additionalProperties": false
      }
    }
  },
  "required": ["is_bank_statement", "transactions"],
  "additionalProperties": false
}
```
