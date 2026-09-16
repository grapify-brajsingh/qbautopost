using System.Globalization;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Models;

namespace QbAutopost.Core.Extract;

/// <summary>One page group sent to Hermes T2.</summary>
public sealed record StatementChunk(int FirstPage, int LastPage, string Text);

/// <summary>
/// PDF statement text → rows through Hermes T2 (spec FR-3, §9.2). Text over <see cref="ChunkLimit"/> characters is sent
/// in page groups; the parts' rows are joined in order. Any Hermes failure, or parts that disagree, hold the statement
/// (spec §9: T2 holds, it never falls back). Amounts and balances are copied from the answer, never computed.
/// </summary>
public sealed class StatementLlmExtractor(IHermesClient hermes, PromptLibrary prompts)
{
    public const string Layout = "hermes-t2";
    public const int ChunkLimit = 60_000;

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    public async Task<StatementParseResult> ExtractAsync(PdfTextResult text, string? auditDir, CancellationToken ct)
    {
        var chunks = Chunk(text);
        var systemPrompt = prompts.Render(HermesTask.Statement, NoValues);
        var answers = new List<StatementAnswer>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var part = $"part {i + 1} of {chunks.Count} (pages {chunks[i].FirstPage}-{chunks[i].LastPage})";
            var content = $"Statement file: {text.File}\nThis is {part}.\n\n{chunks[i].Text}";
            try
            {
                answers.Add(await hermes.CompleteJsonAsync<StatementAnswer>(
                    new HermesRequest(HermesTask.Statement, systemPrompt, content, auditDir), ct));
            }
            catch (HermesException ex)
            {
                return Held(text.File, HoldReasons.HermesFailed, $"{part}: {ex.Message}");
            }
        }

        return Merge(text.File, answers);
    }

    /// <summary>
    /// All pages in one chunk when the joined text fits <see cref="ChunkLimit"/>; otherwise consecutive page groups that
    /// each fit. A single page over the limit is sent on its own (pages are never cut).
    /// </summary>
    public static IReadOnlyList<StatementChunk> Chunk(PdfTextResult text)
    {
        var chunks = new List<StatementChunk>();
        var group = new List<PdfPageText>();
        var length = 0;
        foreach (var page in text.Pages)
        {
            var added = group.Count == 0 ? page.Text.Length : length + PdfTextResult.PageBreak.Length + page.Text.Length;
            if (group.Count > 0 && added > ChunkLimit)
            {
                chunks.Add(ToChunk(group));
                group.Clear();
                added = page.Text.Length;
            }

            group.Add(page);
            length = added;
        }

        if (group.Count > 0)
        {
            chunks.Add(ToChunk(group));
        }

        return chunks;
    }

    private static StatementChunk ToChunk(List<PdfPageText> pages) =>
        new(pages[0].Number, pages[^1].Number, string.Join(PdfTextResult.PageBreak, pages.Select(p => p.Text)));

    private static StatementParseResult Merge(string fileName, IReadOnlyList<StatementAnswer> answers)
    {
        // SPEC-GAP T-303: statement-level fields must agree across parts (null = not shown in that part).
        var errors = new List<string>();
        var kind = Agree("kind", answers.Select(a => (SourceKind?)a.SourceKind), errors);
        var answerLast4 = Agree("accountLast4", answers.Select(a => a.AccountLast4), errors);
        var totals = new StatementTotals(
            Agree("periodStart", answers.Select(a => StatementAnswer.ToDate(a.PeriodStart)), errors),
            Agree("periodEnd", answers.Select(a => StatementAnswer.ToDate(a.PeriodEnd)), errors),
            Agree("openingBalance", answers.Select(a => a.OpeningBalance), errors),
            Agree("closingBalance", answers.Select(a => a.ClosingBalance), errors),
            Agree("transactionCount", answers.Select(a => a.TransactionCount), errors));
        if (errors.Count > 0)
        {
            return Held(fileName, HoldReasons.ExtractionConflict, errors.ToArray());
        }

        // Spec F5: the file name's last-four wins; the statement text must not contradict it.
        var fromName = Last4Detector.FromFileName(fileName);
        if (fromName is not null && answerLast4 is not null && fromName != answerLast4)
        {
            return Held(fileName, HoldReasons.ConflictingLast4, $"file name says {fromName}, statement text says {answerLast4}") with
            {
                Kind = kind,
            };
        }

        var last4 = fromName ?? answerLast4;
        var rows = answers
            .SelectMany(a => a.Rows!)
            .Select((row, i) => new StatementLine
            {
                SourceFile = fileName,
                Kind = kind!.Value,
                Last4 = last4 ?? "",
                LineNo = i + 1,
                Date = StatementAnswer.ToDate(row!.Date)!.Value,
                Description = row.Description!.Trim(),
                Direction = StatementAnswer.ParseDirection(row.Direction!),
                Amount = row.Amount!.Value,
                CheckNo = string.IsNullOrWhiteSpace(row.CheckNo) ? null : row.CheckNo.Trim(),
                Balance = row.Balance,
            })
            .ToList();

        return new StatementParseResult
        {
            File = fileName,
            Kind = kind,
            Last4 = last4,
            Layout = Layout,
            Rows = rows,
            Totals = totals,
            HoldReason = last4 is null ? HoldReasons.UnknownAccount : null,
            Errors = last4 is null ? ["no last-four in the file name or the statement text"] : [],
        };
    }

    /// <summary>The one non-null value all parts report (null when none does); records an error when parts differ.</summary>
    private static T? Agree<T>(string field, IEnumerable<T?> values, List<string> errors)
    {
        var distinct = values.Where(v => v is not null).Distinct().ToList();
        if (distinct.Count > 1)
        {
            errors.Add($"parts disagree on {field}: {string.Join(" / ", distinct.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))}");
            return default;
        }

        return distinct.FirstOrDefault();
    }

    private static StatementParseResult Held(string fileName, string reason, params string[] errors) => new()
    {
        File = fileName,
        Layout = Layout,
        HoldReason = reason,
        Errors = errors,
    };
}
