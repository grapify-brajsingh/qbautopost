using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Api.Extraction;

/// <summary>What <c>GET /api/v2/health</c> says about the upload (dashboard-api-m12 8).</summary>
public static class ExtractionState
{
    public const string Disabled = "disabled";
    public const string Configured = "configured";
    public const string Misconfigured = "misconfigured";

    /// <summary>
    /// <c>disabled</c> while <c>Extraction:Enabled</c> is false; <c>misconfigured</c> when it is on but OpenRouter is off or
    /// has no key; else <c>configured</c>. Read from settings only: it never calls OpenRouter.
    /// </summary>
    public static string Of(AppSettings settings) =>
        !settings.Extraction.Enabled ? Disabled
        : settings.OpenRouter.Enabled && !string.IsNullOrWhiteSpace(settings.OpenRouter.ApiKey) ? Configured
        : Misconfigured;
}

/// <summary>
/// Dashboard-api-m12 6.1 to 6.9: one uploaded PDF in, the tidied rows and possible duplicates out. The PDF and its text
/// live only in this call: nothing of them is logged, stored or copied (anti-goal 4); the model call writes no audit
/// copies (<c>AuditDir: null</c>). Rule 3: amounts are transcribed by the model and tidied, never computed;
/// <c>controlTotal</c> is always null.
/// </summary>
public sealed class StatementExtractionService(
    IHermesClient model,
    IUploadStore store,
    SavedRequestIntake intake,
    ExtractionLimiter limiter,
    ExtractionPrompt prompt,
    IOptions<AppSettings> settings,
    IClock clock,
    ILogger<StatementExtractionService> log)
{
    public const string SchemaName = "bank_statement";

    public bool IsConfigured => ExtractionState.Of(settings.Value) == ExtractionState.Configured;

    public async Task<ExtractionOutcome> ExtractAsync(string userId, string fileName, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var s = settings.Value;
        var watch = Stopwatch.StartNew();
        var pages = 0;
        var rows = 0;
        ExtractionOutcome outcome;
        try
        {
            outcome = await RunAsync(userId, fileName, bytes, s, p => pages = p, r => rows = r, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log(userId, bytes.Length, pages, s.Extraction.Model, watch, rows, "cancelled");
            throw;
        }

        Log(userId, bytes.Length, pages, s.Extraction.Model, watch, rows, outcome.Outcome);
        return outcome;
    }

    private async Task<ExtractionOutcome> RunAsync(
        string userId, string fileName, ReadOnlyMemory<byte> bytes, AppSettings s, Action<int> pages, Action<int> rows,
        CancellationToken ct)
    {
        var x = s.Extraction;
        if (ExtractionState.Of(s) != ExtractionState.Configured)
        {
            return ExtractionOutcome.Unavailable();
        }

        if (bytes.Length > x.MaxBytes)
        {
            return ExtractionOutcome.TooManyBytes(x.MaxBytes);
        }

        var read = UploadPdfReader.Read(bytes, Math.Max(1, x.MaxPages));
        pages(read.PageCount);
        switch (read.Outcome)
        {
            case UploadPdfOutcome.NotAPdf:
                return ExtractionOutcome.NotAPdf();
            case UploadPdfOutcome.Protected:
                return ExtractionOutcome.Protected();
            case UploadPdfOutcome.TooManyPages:
                return ExtractionOutcome.TooManyPages(x.MaxPages);
            case UploadPdfOutcome.NoText:
                return ExtractionOutcome.NoText();
        }

        using var slot = limiter.TryStart(userId, clock.UtcNow, out var retryAfter);
        if (slot is null)
        {
            return ExtractionOutcome.TooMany(retryAfter);
        }

        ExtractionAnswer answer;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, x.TimeoutSeconds)));
            try
            {
                answer = await model.CompleteJsonAsync<ExtractionAnswer>(
                    new HermesRequest(
                        HermesTask.Extraction,
                        prompt.SystemPrompt,
                        read.Text,
                        AuditDir: null,
                        Format: new ResponseFormat(SchemaName, prompt.Schema, Strict: x.StrictSchema),
                        Model: x.Model),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ExtractionOutcome.Timeout(ExtractionOutcome.ExtractionTimeout, x.TimeoutSeconds);
            }
            catch (HermesUnavailableException ex) when (ex.InnerException is TimeoutException)
            {
                // OpenRouter:TimeoutSeconds, the client's own per-call bound, ran out first: the detail names that one.
                return ExtractionOutcome.Timeout(ExtractionOutcome.OpenRouterTimeout, s.OpenRouter.TimeoutSeconds);
            }
            catch (HermesException)
            {
                // Twice invalid (HermesValidationException) or an HTTP / network failure (HermesUnavailableException,
                // never retried by the client). The exception text is not logged here: the line below says enough.
                return ExtractionOutcome.Failed();
            }
        }

        if (answer.IsBankStatement != true)
        {
            return ExtractionOutcome.NotABankStatement();
        }

        var payload = ExtractionTidy.Tidy(answer);
        var element = JsonSerializer.SerializeToElement(payload);

        // SPEC-GAP T-1212b: a reply holding U+0000 cannot be stored as jsonb; nothing is stored and the upload fails as
        // an unusable reply rather than an outage (the behaviour that posts less).
        if (SavedRequestIntake.NotStorableReason(element) is not null)
        {
            return ExtractionOutcome.Failed();
        }

        var now = Microseconds(clock.UtcNow);
        rows(payload["transactions"]?.AsArray().Count ?? 0);

        // 7.3: expiry runs in-line, first, on every successful extraction.
        await store.DeleteExpiredExtractionsAsync(now, ct);

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        var reference = payload["reference"]?.GetValue<string>();
        var since = DateTime.SpecifyKind(now.AddDays(-Math.Max(0, s.SavedRequests.DuplicateLookbackDays)), DateTimeKind.Utc);
        var candidates = await store.UploadDuplicateCandidatesAsync(sha256, since, ct);
        var hits = UploadDuplicateMatcher.Match(
            sha256, reference, intake.RowFingerprintsOf(element, now), UploadDuplicateMatcher.RowKeysOf(element), candidates);

        var record = new StatementExtraction(
            Guid.NewGuid(),
            userId,
            UploadFileName.Sanitize(fileName),
            bytes.Length,
            sha256,
            read.PageCount,
            x.Model,
            prompt.PromptVersion,
            payload,
            now,
            now.AddHours(Math.Max(0, x.RecordHours)),
            null);
        await store.CreateExtractionAsync(record, ct);
        return ExtractionOutcome.Ok(StatementExtractionView.Of(record, hits));
    }

    private void Log(string userId, long size, int pages, string modelName, Stopwatch watch, int rows, string outcome) =>
        log.LogInformation(
            "Statement extraction for user {UserId}: {SizeBytes} bytes, {PageCount} pages, model {Model}, prompt {PromptVersion}, "
            + "{ElapsedMs} ms, {RowCount} rows, outcome {Outcome}",
            userId, size, pages, modelName, prompt.PromptVersion, watch.ElapsedMilliseconds, rows, outcome);

    /// <summary>PostgreSQL keeps microseconds; the answer shows what the record holds.</summary>
    private static DateTime Microseconds(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
}

/// <summary>The answer of one extraction: the §6.8 body, or one §6.1 / §6.9 problem.</summary>
public sealed record ExtractionOutcome(
    int Status, string Outcome, string? Title, string? Detail, TimeSpan? RetryAfter, StatementExtractionView? View)
{
    /// <summary>The problem <c>type</c>; the outcome names are the spec's problem types.</summary>
    public string? Type => View is null ? Outcome : null;

    public static ExtractionOutcome Ok(StatementExtractionView view) => new(200, "ok", null, null, null, view);

    public static ExtractionOutcome Unavailable() => Problem(
        503, "extraction-unavailable", "Extraction unavailable",
        "Reading uploaded statements is not configured on this server. Nothing was read or stored.");

    public static ExtractionOutcome InvalidRequest(string detail) => Problem(400, "invalid-request", "Invalid request", detail);

    public static ExtractionOutcome TooManyBytes(long max) => Problem(
        413, "file-too-large", "File too large",
        string.Create(CultureInfo.InvariantCulture, $"The file may be at most {max} bytes. Nothing was read or stored."));

    public static ExtractionOutcome TooManyPages(int max) => Problem(
        413, "file-too-large", "File too large",
        string.Create(CultureInfo.InvariantCulture, $"The PDF may have at most {max} pages. Nothing was read or stored."));

    public static ExtractionOutcome NotAPdf() => Problem(
        415, "not-a-pdf", "Not a PDF", "The file is not a PDF that can be opened. Nothing was read or stored.");

    public static ExtractionOutcome Protected() => Problem(
        422, "pdf-protected", "PDF is protected",
        "The PDF is encrypted or password-protected. Upload an unprotected copy. Nothing was stored.");

    public static ExtractionOutcome NoText() => Problem(
        422, "no-text", "No readable text", "This PDF has no readable text. It may be a scan.");

    public static ExtractionOutcome TooMany(TimeSpan retryAfter) => new(
        429, "too-many-extractions", "Too many extractions",
        "Wait for your other upload to finish, or try again later. Nothing was read or stored.", retryAfter, null);

    public static ExtractionOutcome NotABankStatement() => Problem(
        422, "not-a-bank-statement", "Not a bank statement", "This PDF doesn't look like a bank or card statement.");

    public static ExtractionOutcome Failed() => Problem(
        502, "extraction-failed", "Extraction failed", "The statement could not be read. Try again. Nothing was stored.");

    public const string ExtractionTimeout = "Extraction:TimeoutSeconds";
    public const string OpenRouterTimeout = "OpenRouter:TimeoutSeconds";

    /// <summary>504; the detail names the setting whose bound actually ran out, with its value.</summary>
    public static ExtractionOutcome Timeout(string setting, int seconds) => Problem(
        504, "extraction-timeout", "Extraction timed out",
        string.Create(CultureInfo.InvariantCulture, $"No answer within {seconds} s ({setting}). Try again. Nothing was stored."));

    private static ExtractionOutcome Problem(int status, string type, string title, string detail) =>
        new(status, type, title, detail, null, null);
}

public sealed record ExtractionFileView(string Name, long SizeBytes, string Sha256, int PageCount);

public sealed record ExtractionDuplicateView(
    long Id, string PublicRef, string? Reference, string Status, DateTime ReceivedUtc, string Source, IReadOnlyList<string> Reasons);

/// <summary>Dashboard-api-m12 6.8 (R-13.7), in its key order.</summary>
public sealed record StatementExtractionView(
    Guid ExtractionId,
    ExtractionFileView File,
    string Model,
    string PromptVersion,
    DateTime ExtractedUtc,
    DateTime ExpiresUtc,
    bool IsBankStatement,
    JsonObject Payload,
    IReadOnlyList<ExtractionDuplicateView> PossibleDuplicates)
{
    public static StatementExtractionView Of(StatementExtraction e, IReadOnlyList<UploadDuplicateHit> hits) => new(
        e.Id,
        new ExtractionFileView(e.FileName, e.SizeBytes, e.Sha256, e.PageCount),
        e.Model,
        e.PromptVersion,
        e.ExtractedUtc,
        e.ExpiresUtc,
        true,
        (JsonObject)e.Payload.DeepClone(),
        hits.Select(h => new ExtractionDuplicateView(
            h.Id,
            string.Create(CultureInfo.InvariantCulture, $"SR-{h.Id:000000}"),
            h.Reference,
            SavedRequestJson.Name(h.Status),
            h.ReceivedUtc,
            h.Source,
            h.Reasons)).ToList());
}
