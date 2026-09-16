namespace QbAutopost.Core.Models;

/// <summary>Reason codes written to <c>analysis.json</c>/<c>result.json</c> for held or skipped items.</summary>
public static class HoldReasons
{
    // Spec-defined codes.
    public const string UnknownAccount = "unknown-account";
    public const string UnknownPayee = "unknown-payee";
    public const string UnknownCsvLayout = "unknown-csv-layout";
    public const string UnsupportedExtension = "unsupported-extension";
    public const string ScannedPdfOcrDisabled = "scanned-pdf-ocr-disabled";
    public const string AlreadyPosted = "already-posted";
    public const string AlreadyInQuickBooks = "already-in-quickbooks";
    public const string PossibleDuplicate = "possible-duplicate";
    public const string Ambiguous = "ambiguous";

    // SPEC-GAP T-002: codes below are not named in the spec; each holds rather than guesses (see tracker Questions).
    public const string AmbiguousCsvLayout = "ambiguous-csv-layout";
    public const string UnparsableRows = "unparsable-rows";
    public const string ConflictingLast4 = "conflicting-last4";
    public const string RefNumberTooLong = "refnumber-too-long";
    public const string NoHoldingAccount = "no-holding-account";
    public const string NoDepositIncomeAccount = "no-deposit-income-account";
    public const string SubfolderIgnored = "subfolder-ignored";

    // SPEC-GAP T-103: pipeline codes (statement and line level), each holding instead of guessing.
    public const string ReconcileFailed = "reconcile-failed";
    public const string KindMismatch = "kind-mismatch";
    public const string KindNotRequested = "kind-not-requested";
    public const string DuplicateLine = "duplicate-line";
    public const string QuickBooksRejected = "quickbooks-rejected";
    public const string QuickBooksNoResponse = "quickbooks-no-response";
    public const string QuickBooksUnavailable = "quickbooks-unavailable";
    public const string AmountMismatch = "amount-mismatch";

    // SPEC-GAP T-301: a statement file the extractor cannot open (corrupt or encrypted workbook) is held whole.
    public const string UnreadableStatement = "unreadable-statement";

    // SPEC-GAP T-303: T2 holds (spec §9 says "hold" on a Hermes failure but names no code).
    public const string HermesFailed = "hermes-failed";
    public const string ExtractionConflict = "extraction-conflict";

    // SPEC-GAP T-401: plan M4 names "unreadable" for an invoice image with OCR disabled; also used for an invoice that
    // cannot be opened, OCR errors, no text, or text too long for one Hermes request.
    public const string Unreadable = "unreadable";

    // SPEC-GAP T-402: why an invoice was not matched (besides the spec's "ambiguous").
    public const string NoMatchingLine = "no-matching-line";
    public const string NoInvoiceDate = "no-invoice-date";

    // SPEC-GAP T-501: T4 has no accounts to choose from (qb-lists.json empty or not synced).
    public const string NoAccounts = "no-accounts";

    // SPEC-GAP T-502: gate G3 holds (FR-7 names none).
    public const string LowConfidence = "low-confidence";
    public const string NoPriorPosting = "no-prior-posting";

    // No tier 1–2 account. The pipeline's ModelTiers pass replaces it (tiers 3–4); it stays only on lines that pass
    // never reaches (e.g. Mapper used alone).
    public const string NoAccountRule = "no-account-rule";
}
