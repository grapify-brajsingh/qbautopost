using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Hermes;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Extract;

/// <summary>Hermes T2 (spec §9.2): PDF text → statement rows, chunked per page group above 60 000 characters.</summary>
public sealed class StatementLlmExtractorTests
{
    private const string FileName = "chase-checking-4521.pdf";
    private const string AuditDir = "audit-dir";

    private static readonly PromptLibrary Prompts =
        PromptLibrary.Load(Path.Combine(AppContext.BaseDirectory, PromptLibrary.DefaultFolder), HermesTask.Statement);

    [Fact]
    public async Task Should_ReturnRowsWithTotals_When_AnswerIsValid()
    {
        var hermes = new ScriptedHermes(_ => Fixtures.Read("hermes", "statement.json"));

        var result = await Extract(hermes, FixtureText());

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(StatementLlmExtractor.Layout, result.Layout);
        Assert.Equal(SourceKind.Bank, result.Kind);
        Assert.Equal("4521", result.Last4);
        Assert.Equal(
            new StatementTotals(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), 13195.87m, 10230.45m, 7),
            result.Totals);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], result.Rows.Select(r => r.LineNo));
        Assert.All(result.Rows, r =>
        {
            Assert.Equal(FileName, r.SourceFile);
            Assert.Equal("4521", r.Last4);
            Assert.Equal(SourceKind.Bank, r.Kind);
        });
    }

    [Fact]
    public async Task Should_ProduceSameRowsAsCsv_When_FixtureTextIsExtracted()
    {
        var csv = new CsvStatementParser(Fixtures.SampleRules().CsvLayouts).Parse(Fixtures.SampleBankCsv);
        var hermes = new ScriptedHermes(_ => Fixtures.Read("hermes", "statement.json"));

        var result = await Extract(hermes, FixtureText());

        Assert.Equal(
            csv.Rows.Reverse().Select(r => (r.Date, r.Description, r.Direction, r.Amount, r.CheckNo, r.Balance, r.Fingerprint)),
            result.Rows.Select(r => (r.Date, r.Description, r.Direction, r.Amount, r.CheckNo, r.Balance, r.Fingerprint)));
    }

    [Fact]
    public async Task Should_SendStatementTaskWithFileNameAndAllPages_When_TextIsSmall()
    {
        var hermes = new ScriptedHermes(_ => Fixtures.Read("hermes", "statement.json"));
        var text = FixtureText();

        await Extract(hermes, text);

        var request = Assert.Single(hermes.Requests);
        Assert.Equal(HermesTask.Statement, request.Task);
        Assert.Equal(AuditDir, request.AuditDir);
        Assert.DoesNotContain("{{", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains(FileName, request.UserContent, StringComparison.Ordinal);
        Assert.Contains("part 1 of 1 (pages 1-2)", request.UserContent, StringComparison.Ordinal);
        Assert.EndsWith(text.Text, request.UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_KeepOneChunk_When_TextIsExactlyAtTheLimit()
    {
        var pages = Pages(30_000, StatementLlmExtractor.ChunkLimit - 30_000 - PdfTextResult.PageBreak.Length);

        var chunks = StatementLlmExtractor.Chunk(pages);

        var chunk = Assert.Single(chunks);
        Assert.Equal((1, 2), (chunk.FirstPage, chunk.LastPage));
        Assert.Equal(StatementLlmExtractor.ChunkLimit, chunk.Text.Length);
    }

    [Fact]
    public void Should_SplitByPageGroups_When_TextExceedsTheLimit()
    {
        var pages = Pages(25_000, 25_000, 25_000, 10_000);

        var chunks = StatementLlmExtractor.Chunk(pages);

        Assert.Equal([(1, 2), (3, 4)], chunks.Select(c => (c.FirstPage, c.LastPage)));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= StatementLlmExtractor.ChunkLimit));
        Assert.Equal(pages.Text, string.Join(PdfTextResult.PageBreak, chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Should_KeepPageWhole_When_OnePageExceedsTheLimit()
    {
        var pages = Pages(1_000, 70_000, 1_000);

        var chunks = StatementLlmExtractor.Chunk(pages);

        Assert.Equal([(1, 1), (2, 2), (3, 3)], chunks.Select(c => (c.FirstPage, c.LastPage)));
    }

    [Fact]
    public async Task Should_MergeRowsInPartOrder_When_StatementIsChunked()
    {
        var hermes = new ScriptedHermes(r => r.UserContent.Contains("part 1 of 2", StringComparison.Ordinal)
            ? Answer(opening: 100.00m, closing: 60.00m, count: 3, rows: [Row("2026-08-02", "FIRST", 10m), Row("2026-08-03", "SECOND", 20m)])
            : Answer(closing: 60.00m, rows: [Row("2026-08-04", "THIRD", 10m)]));

        var result = await Extract(hermes, Pages(40_000, 40_000));

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(2, hermes.Requests.Count);
        Assert.Contains("part 2 of 2 (pages 2-2)", hermes.Requests[1].UserContent, StringComparison.Ordinal);
        Assert.Equal(["FIRST", "SECOND", "THIRD"], result.Rows.Select(r => r.Description));
        Assert.Equal([1, 2, 3], result.Rows.Select(r => r.LineNo));
        Assert.Equal(new StatementTotals(null, null, 100.00m, 60.00m, 3), result.Totals);
    }

    [Fact]
    public async Task Should_HoldAsExtractionConflict_When_PartsDisagreeOnABalance()
    {
        var hermes = new ScriptedHermes(r => r.UserContent.Contains("part 1 of 2", StringComparison.Ordinal)
            ? Answer(opening: 100.00m)
            : Answer(opening: 90.00m));

        var result = await Extract(hermes, Pages(40_000, 40_000));

        Assert.Equal(HoldReasons.ExtractionConflict, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("openingBalance", StringComparison.Ordinal));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task Should_HoldAsExtractionConflict_When_PartsDisagreeOnKind()
    {
        var hermes = new ScriptedHermes(r => Answer(kind: r.UserContent.Contains("part 1 of 2", StringComparison.Ordinal) ? "bank" : "card"));

        var result = await Extract(hermes, Pages(40_000, 40_000));

        Assert.Equal(HoldReasons.ExtractionConflict, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("kind", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_HoldAsConflictingLast4_When_AnswerDisagreesWithFileName()
    {
        var hermes = new ScriptedHermes(_ => Answer(last4: "9999"));

        var result = await Extract(hermes, FixtureText());

        Assert.Equal(HoldReasons.ConflictingLast4, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("9999", StringComparison.Ordinal) && e.Contains("4521", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_UseAnswerLast4_When_FileNameHasNone()
    {
        var hermes = new ScriptedHermes(_ => Answer(last4: "7788", kind: "card"));

        var result = await Extract(hermes, FixtureText(), "august-card.pdf");

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal("7788", result.Last4);
        Assert.Equal(SourceKind.Card, result.Kind);
        Assert.All(result.Rows, r => Assert.Equal("7788", r.Last4));
    }

    [Fact]
    public async Task Should_HoldAsUnknownAccount_When_NeitherFileNameNorAnswerHasLast4()
    {
        var hermes = new ScriptedHermes(_ => Answer(last4: null));

        var result = await Extract(hermes, FixtureText(), "august.pdf");

        Assert.Equal(HoldReasons.UnknownAccount, result.HoldReason);
        Assert.Equal(SourceKind.Bank, result.Kind);
    }

    [Fact]
    public async Task Should_ReturnNoRows_When_StatementHasNoTransactions()
    {
        var hermes = new ScriptedHermes(_ => Answer(opening: 50.00m, closing: 50.00m, count: 0));

        var result = await Extract(hermes, FixtureText());

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task Should_HoldAsHermesFailed_When_AnswerFailsValidationTwice()
    {
        var hermes = new ScriptedHermes(_ => """{ "kind": "bank", "rows": [ { "date": "2026-08-02", "description": "X", "direction": "debit", "amount": -5 } ] }""");

        var result = await Extract(hermes, FixtureText());

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("part 1 of 1", StringComparison.Ordinal) && e.Contains("rows[0].amount", StringComparison.Ordinal));
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task Should_HoldAsHermesFailedAndStop_When_HermesIsUnavailable()
    {
        var hermes = new ScriptedHermes(_ => throw new HermesUnavailableException(HermesTask.Statement, "HTTP 503"));

        var result = await Extract(hermes, Pages(40_000, 40_000));

        Assert.Equal(HoldReasons.HermesFailed, result.HoldReason);
        Assert.Contains(result.Errors, e => e.Contains("HTTP 503", StringComparison.Ordinal));
        Assert.Single(hermes.Requests);
    }

    [Fact]
    public async Task Should_Propagate_When_Cancelled()
    {
        var hermes = new ScriptedHermes(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Extract(hermes, FixtureText()));
    }

    [Fact]
    public async Task Should_ReturnRows_When_RealClientSucceedsOnRetry()
    {
        using var handler = new StubHttpHandler()
            .ReplyContent("""{ "kind": "bank", "rows": [ { "date": "08/02/2026", "description": "X", "direction": "debit", "amount": 5 } ] }""")
            .ReplyContent(Fixtures.Read("hermes", "statement.json"));
        using var http = new HttpClient(handler);
        var client = new HermesClient(http, new HermesOptions { BaseUrl = "http://hermes.test" });

        var result = await new StatementLlmExtractor(client, Prompts).ExtractAsync(FixtureText(), null, CancellationToken.None);

        Assert.False(result.IsHeld, string.Join("; ", result.Errors));
        Assert.Equal(7, result.Rows.Count);
        Assert.Contains("rows[0].date", handler.Requests[1].Message(1), StringComparison.Ordinal);
    }

    private static Task<StatementParseResult> Extract(IHermesClient hermes, PdfTextResult text, string? fileName = null) =>
        new StatementLlmExtractor(hermes, Prompts).ExtractAsync(text with { File = fileName ?? FileName }, AuditDir, CancellationToken.None);

    /// <summary>The fixture statement text as <c>PdfText</c> would return it (pages split at <c>&lt;&lt;PAGE&gt;&gt;</c>).</summary>
    private static PdfTextResult FixtureText() => new()
    {
        File = FileName,
        Pages = Fixtures.Read("statements", "chase-checking-4521.pdf.txt")
            .ReplaceLineEndings("\n")
            .Split("<<PAGE>>\n")
            .Select((text, i) => new PdfPageText(i + 1, text.Trim(), false, 0))
            .ToList(),
    };

    private static PdfTextResult Pages(params int[] lengths) => new()
    {
        File = FileName,
        Pages = lengths.Select((length, i) => new PdfPageText(i + 1, new string((char)('a' + i), length), false, 0)).ToList(),
    };

    private static object Row(string date, string description, decimal amount, string direction = "debit") =>
        new { date, description, direction, amount, checkNo = (string?)null, balance = (decimal?)null };

    private static string Answer(
        string? kind = "bank",
        string? last4 = "4521",
        decimal? opening = null,
        decimal? closing = null,
        int? count = null,
        object[]? rows = null) =>
        JsonSerializer.Serialize(new
        {
            accountLast4 = last4,
            kind,
            periodStart = (string?)null,
            periodEnd = (string?)null,
            openingBalance = opening,
            closingBalance = closing,
            transactionCount = count,
            rows = rows ?? [],
        });
}
