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

    // TODO(T-502): replaced by Hermes T4 tiers 3–4; until then a line without a rule/history account is held.
    public const string NoAccountRule = "no-account-rule";
}
