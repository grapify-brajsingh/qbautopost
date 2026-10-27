# Spec — Validate and post transaction data (synchronous)

**Status**: **implemented** 2026-09-24 (T-921…T-924), `ready-for-human` — verified against `FakeQbGateway` only, so
the server checklist in `docs/testing/T-921-T-924.tdd.md` §9 is still outstanding. §10's questions remain open and
are tracked as **Q-74…Q-77** (Q-A…Q-D in order), with the conservative choice taken for each. **Q-78** is a new gap
this document does not cover: §6.1 promises `status: posted` on a 200, but §5.2 keeps the gates that run *after* the
SDK is touched, and a rejection there has no ending in §5.
**Owner request**: *"i want only api which will not be dryrun, it will first validate the payload, return required
validation results if any, then post the data directly to quickbooks … it should not be queued or batched, it will be
synchronous."*
**Contract rank**: subordinate to `docs/spec.md` (FR-x) and `docs/spec-api-v1.md`. Where this document and
`spec-api-v1.md` §6.1 differ, **this one governs its own route only** — FR-A-8 is unchanged for `POST
/api/v1/quickbooks/transactions`.
**Decision record**: `docs/adr/0008-synchronous-validate-and-post.md`.

---

## 1. What this is, in one paragraph

One route that does what an operator means by "post this": check the data, and if there is anything to say about it,
say it and post nothing; otherwise post it to QuickBooks and answer with the transaction ids. One request, one
response, no dry run, no batch to poll, no place in a queue.

## 2. What it is not

Three things this deliberately does **not** do, each because something in the system would break otherwise.

### 2.1 It is not unserialised

The QuickBooks SDK holds **one session at a time** (ADR-0006) and blocks while QuickBooks is busy. Two overlapping
posts, or one post overlapping a folder job, is a collision — not a slow request. So SDK access stays serialised.

What changes is what happens when the lock is held: today `RunExclusiveAsync` **enqueues and waits**; here the request
is **refused immediately** with `409`. That is what "not queued" means and is the point of ADR-0008 — the caller never
sits in a line they cannot see.

### 2.2 It is not unrecorded

"Not batched" removes the batch as a *resource* — no `batchId` to poll, no `GET /batches/{id}` lifecycle, no `202`.
It cannot remove the **ledger row**, because two safety properties read it:

- **G4** compares every new fingerprint against the ledger. Without the row, a retry of a request whose response was
  lost posts everything a second time.
- **Undo** (FR-13) reverses a post from the `TxnID`/`EditSequence` pairs the ledger holds. Without the row there is
  nothing to undo.

The evidence folder also stays: CLAUDE.md rule 7 requires the request on disk **before** the first qbXML leaves the
process, so a crash mid-post leaves proof of what was asked for.

### 2.3 It is not a dry run, and cannot be asked to be one

`dryRun` is **not a field on this request**. A caller who sends it gets `400`, rather than a silently ignored field
that might leave them believing nothing was posted. `POST /api/v1/quickbooks/transactions/validate` remains the
offline check and is unchanged.

## 3. Route

```
POST /api/v1/quickbooks/transactions/validate-and-post
```

| | |
|---|---|
| Scope | `qb:post` |
| Idempotency | `Idempotency-Key` honoured (api-v1 §6.5), **strongly recommended** — see §8 |
| `Prefer: respond-async` | **ignored**; this route has no asynchronous form |
| Rate limit | the posting bucket, `Api:RateLimits:PostPerMinute` |

## 4. Request

Identical to api-v1 §6.1 **minus `dryRun`**. The row shape, the fingerprint, and every field rule are unchanged, so a
caller can move between the two routes without rewriting rows.

```json
{
  "reference": "zemen-2019-12",
  "controlTotal": 4820.17,
  "transactions": [
    {
      "externalId": "row-7",
      "kind": "Check",
      "date": "2026-08-05",
      "amount": 184.32,
      "account": "Chase Checking 4521",
      "lineAccount": "Repairs and Maintenance",
      "payee": "ABC Plumbing LLC",
      "checkNo": "1042",
      "refNumber": "1042",
      "memo": "INV 88213 plumbing repair unit 4",
      "last4": "4521"
    }
  ]
}
```

| Field | Rule |
|---|---|
| `reference` | optional; `^[A-Za-z0-9._-]{1,64}$`. Names the ledger row as `api-<reference>`. |
| `controlTotal` | **required**. Must equal the sum of `amount` to the cent. Never adjusted (CLAUDE.md rule 3). |
| `dryRun` | **refused.** Present at all → `400 dry-run-not-supported`. |
| `allowModelAccounts` | **refused**, as T-915 (`400`). Tiers 3–4 do not run here. |
| `transactions[]` | as api-v1 §6.1: `kind`, `date`, `amount`, `account` required; `lineAccount`, `payee`, `checkNo`, `refNumber`, `memo`, `last4`, `externalId` optional. |

Limits: `Api:MaxTransactionsPerRequest` rows (default 500), body ≤ 2 MB.

## 5. Behaviour

Exactly one of five outcomes. Nothing partially happens.

```
read request ─┬─ malformed / control total / limits ──────────────► 400, nothing posted
              │
              ├─ worker busy ─────────────────────────────────────► 409, nothing posted
              │
              └─ plan (FR-6 map, G3, G4 ledger half, name check)
                     │
                     ├─ ANY row not postable ─────────────────────► 422, nothing posted
                     │
                     └─ every row postable
                            ├─ backup stale / QuickBooks down ────► 503, nothing posted
                            └─ G4 live half → qbXML → SDK → G5 ──► 200, all posted
```

### 5.1 The all-or-nothing gate — the one real change

**If any row would be held, the whole request is refused and nothing is sent.** This is the owner's "return required
validation results if any, *then* post", read literally.

It differs from FR-A-8, where one held row does not stop the other 133. The trade is deliberate and it is not free:

- **For it**: an operator gets one answer about one batch. There is no half-posted state to reconcile, and no risk of
  believing a batch landed when a tenth of it was held. It posts *less*, which is the direction rule 4 requires.
- **Against it**: one unresolved payee blocks 133 good rows. On a 134-row statement that is a real cost.

Mitigation, not a fix: `POST /api/v1/quickbooks/transactions/validate` is offline and unchanged, so the caller can
find every problem before sending. The two routes take the same body.

### 5.2 Gates

Every gate from api-v1 §6.2 applies unchanged — G3 confidence, G4 duplicates, G5 verify, the FR-11 backup guard, and
`controlTotal` in place of G1. The only difference is **when a failure stops the request**: here, any one of them
holding any one row ends it at §5.1 before the SDK is touched.

## 6. Responses

### 6.1 `200` — posted

```json
{
  "postId": "api-zemen-2019-12",
  "reference": "zemen-2019-12",
  "status": "posted",
  "companyFile": "D:\\...\\Tropicana.QBW",
  "counts": { "submitted": 12, "posted": 12 },
  "totals": { "submitted": 4820.17, "posted": 4820.17 },
  "posted": [
    { "index": 0, "externalId": "row-7", "requestId": "9f2c1ab4d0e5f678",
      "txnId": "8A1-1758654321", "editSequence": "1758654321",
      "kind": "Check", "account": "Repairs and Maintenance", "amount": 184.32 }
  ],
  "startedUtc": "2026-09-24T09:14:02Z",
  "finishedUtc": "2026-09-24T09:14:37Z"
}
```

`status` is always `posted` on a `200`. The enum's other values cannot occur here: `partial` is impossible by §5.1,
`ready` needs a dry run, and `failed` is an error status code. **`postId` is not a polling resource** — no `GET`
accepts it. It identifies the ledger row for undo.

### 6.2 `422` — validation, nothing posted

```json
{
  "type": "about:blank",
  "title": "The transactions were not posted",
  "status": 422,
  "detail": "3 of 134 rows would be held; nothing was sent to QuickBooks.",
  "requestId": "01M37Z37648AS0CDXT7JW5FV9R",
  "counts": { "submitted": 134, "postable": 131, "held": 3 },
  "validation": [
    { "index": 3, "externalId": "row-4", "reason": "no-account-rule",
      "detail": "no rule resolves an account for this description",
      "candidates": ["Repairs and Maintenance", "Utilities"] },
    { "index": 9, "externalId": "row-10", "reason": "unknown-payee",
      "detail": "not in qb-lists.json: NEVER HEARD OF THEM" },
    { "index": 40, "externalId": "row-41", "reason": "date-too-old",
      "detail": "2019-12-11 is more than 3000 days ago" }
  ],
  "unknownNames": { "accounts": [], "vendors": ["NEVER HEARD OF THEM"], "customers": [] }
}
```

Problem details per RFC 7807, with the findings in `validation`. **Every** problem is listed, not the first — a caller
fixing them one round-trip at a time is the failure mode this format exists to avoid.

### 6.3 `400` — the request could not be read

Control-total mismatch, too many rows, a malformed row, `dryRun` present, `allowModelAccounts` present. Nothing is read
out of a refused request.

### 6.4 `409` — busy

```json
{
  "title": "QuickBooks is busy with another operation",
  "status": 409,
  "detail": "A folder job or another post is using the QuickBooks session. Nothing was sent. Retry in a few seconds.",
  "retryAfterSeconds": 5
}
```

`Retry-After` is also set as a header. **Nothing was sent**, so a retry is safe.

### 6.5 `503` — cannot post right now

Backup stale (FR-11), QuickBooks unreachable, or the rules file missing (T-920). Nothing posted in every case.

## 7. Timing

The caller holds the connection for the whole post. A 134-row batch is minutes, not milliseconds.

- There is **no server-side cut-off that abandons the caller**: `Api:SyncPostTimeoutSeconds` governs the `202` path of
  FR-A-10 and **does not apply here**, because there is no `202` to fall back to.
- Clients must set a request timeout **longer than the largest batch they send**. A recommended figure cannot be
  invented without measurement — see Q-B.
- If the client disconnects, **the post continues to completion** and the ledger row is written. This is deliberate:
  abandoning a half-sent qbXML would leave QuickBooks in a state nobody recorded. The caller recovers with §8.

## 8. Retries and the lost response

The one genuinely dangerous case: the post succeeds, the response is lost, the caller retries.

Three things stand in the way of a double post, and a caller should use all three:

1. **`Idempotency-Key`** — a replay within `Api:IdempotencyRetentionDays` returns the original response without
   re-posting. This is the only mechanism that returns the *original* `txnId`s.
2. **G4 + the ledger** — a repeat fingerprint inside `DuplicateWindowDays` is caught even without the key. But under
   §5.1 a duplicate row is a *held* row, so the retry answers `422`, not `200`. The caller must read it as "already
   posted", which is why the key is better.
3. **`externalId`** — echoed on every posted row, so a caller can reconcile against their own records.

**Without `Idempotency-Key`, a retry after a lost response is not distinguishable by the caller from a first attempt.**
Sending the header is strongly recommended, and this spec stops short of mandating it only because api-v1 §6.5 already
defines it as optional (see Q-C).

## 9. Acceptance criteria

| # | Criterion |
|---|---|
| VP-1 | `dryRun` in the body → `400`; the field does not exist on the request type. |
| VP-2 | One unresolvable row in a batch of many → `422`, `validation` names it, and **the gateway received nothing**. |
| VP-3 | Every row postable → `200`, every row carries a `txnId`, `counts.posted == counts.submitted`. |
| VP-4 | A control total that does not match → `400`; no amount is altered. |
| VP-5 | Worker busy → `409` with `Retry-After`, **without enqueuing**, and the gateway received nothing. |
| VP-6 | Stale backup → `503`, nothing sent. |
| VP-7 | The request file is on disk before the first qbXML leaves the process (rule 7). |
| VP-8 | A posted request writes one ledger row; `POST /api/v1/batches/{postId}/undo` reverses exactly it. |
| VP-9 | The same body posted twice with one `Idempotency-Key` posts once and returns the same `txnId`s. |
| VP-10 | No test contacts a real QuickBooks or Hermes; the suite passes on Linux (rule 1). |

## 10. Open questions — to answer before implementing

| # | Question | Why it cannot be guessed |
|---|---|---|
| **Q-A** | Is all-or-nothing (§5.1) right for a 134-row statement, where one unresolved payee blocks 133 good rows? | It is the literal reading of the request, and it posts less — but the cost lands on the owner, not on me. The alternative (post the good rows, report the held ones) is exactly FR-A-8, which already exists. |
| **Q-B** | What client timeout should be documented for a full-size batch? | Needs a measurement against a real QuickBooks; the fake gateway returns instantly, so the suite can prove nothing about it. |
| **Q-C** | Should `Idempotency-Key` be **mandatory** on this route (`400` without it)? | §8 makes it the only reliable defence against a lost-response retry. Mandating a header breaks callers that api-v1 §6.5 told it was optional, which is an owner's call. |
| **Q-D** | On `409`, is refusing right, or should a *short* bounded wait (say 2 s) be tried first? | Refusing is the honest reading of "not queued". A brief wait would cut spurious 409s on a single-operator server, but it is a queue with a small number. |

## 11. What this spec does not cover

- **The folder path is untouched.** `POST /api/v1/jobs/{id}/post` and its worker keep their present behaviour.
- **`POST /api/v1/quickbooks/transactions` (FR-A-8) is untouched**, including its `dryRun`, its `202`, and its
  per-row hold semantics. This route is an addition, not a replacement, and nothing here changes what an existing
  caller sees.
- **No new package, no database, no broker, no second background worker** — CLAUDE.md "Don't" list.
- **Hermes is not consulted** on this path, as tiers 3–4 do not run (T-915).
