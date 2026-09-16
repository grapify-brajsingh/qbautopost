using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Extract;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Output;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>FR-1…FR-9 on the sample job (regex spec reader, scripted Hermes T2, no QuickBooks call).</summary>
public sealed class JobPipelineTests : IDisposable
{
    private const string Company = "Tropicana Properties LLC";

    private readonly TempJobFolder _job = TempJobFolder.FromSample();

    private readonly ScriptedHermes _hermes;

    private IOcr _ocr = new DisabledOcr();

    /// <summary>What Hermes T2 answers for every PDF statement (default: the sample bank statement).</summary>
    private string _statementAnswer = Fixtures.Read("hermes", "statement.json");

    /// <summary>T2 answer for the card PDF (<c>chase-card-7788.pdf</c>).</summary>
    private string _cardAnswer = Fixtures.Read("hermes", "statement-card-7788.json");

    /// <summary>What Hermes T3 answers for every invoice (default: the sample Home Depot invoice).</summary>
    private string _invoiceAnswer = Fixtures.Read("hermes", "invoice.json");

    public JobPipelineTests() => _hermes = new ScriptedHermes(r =>
        r.Task == HermesTask.Invoice ? _invoiceAnswer
        : r.UserContent.Contains("chase-card-7788.pdf", StringComparison.Ordinal) ? _cardAnswer
        : _statementAnswer);

    public void Dispose() => _job.Dispose();

    private string LedgerFile => Path.Combine(_job.Root, "ledger.json");

    private JobPipeline Pipeline(string company = Company) => new(
        new PipelineOptions
        {
            CompanyName = company,
            RulesFile = Path.Combine(AppContext.BaseDirectory, "samples", "rules.json"),
            LedgerFile = LedgerFile,
            QbListsFile = Path.Combine(_job.Root, "qb-lists.json"),
        },
        new RegexSpecReader(),
        TestStatementReader.Create(_ocr, _hermes),
        TestInvoiceExtractor.Create(_ocr, _hermes),
        new UnusedGateway(),
        new SystemClock());

    private JobRecord Job() => new() { JobId = "2026-08-tropicana", Folder = _job.Folder, Status = JobStatus.Analysing, DryRun = true };

    private Task<AnalysisResult> Analyse(string company = Company) => Pipeline(company).RunAnalysisAsync(Job(), CancellationToken.None);

    private static string PostableKey(MappedTxn m) =>
        string.Join('|', m.RequestId, m.Kind, m.Account, m.Payee, m.LineAccount, m.RefNumber, m.Line.Amount, m.Line.Date);

    /// <summary>Replaces a sample CSV statement with the committed text PDF of the same statement.</summary>
    private void UsePdf(string name = "chase-checking-4521")
    {
        File.Delete(_job.PathOf("statements", name + ".csv"));
        _job.Copy(Fixtures.PathOf("statements", name + ".pdf"), Path.Combine("statements", name + ".pdf"));
    }

    private void UseBankPdf() => UsePdf();

    private void EditRequirement(Func<string, string> edit)
    {
        var path = _job.PathOf("requirement.txt");
        File.WriteAllText(path, edit(File.ReadAllText(path)));
    }

    [Fact]
    public async Task Should_PostEightHoldTwoSkipOne_When_SampleJobIsAnalysed()
    {
        var analysis = await Analyse();

        Assert.True(analysis.Gate.Ok, string.Join("; ", analysis.Gate.Errors));
        Assert.Equal(8, analysis.ToPost.Count);
        Assert.Equal(2, analysis.Lines.Count(l => l.Decision == Decision.Hold));
        Assert.Equal(1, analysis.Lines.Count(l => l.Decision == Decision.Skip));
    }

    [Fact]
    public async Task Should_WriteEveryOutputFile_When_SampleJobIsAnalysed()
    {
        await Analyse();

        string[] expected =
        [
            "spec.json", "analysis.json", "request.qbxml",
            BatchEnterSheet.ChecksFile, BatchEnterSheet.CreditCardFile, BatchEnterSheet.DepositsFile,
            Path.Combine("statements", "chase-checking-4521.csv.rows.json"),
            Path.Combine("statements", "chase-card-7788.csv.rows.json"),
        ];
        Assert.All(expected, f => Assert.True(File.Exists(_job.PathOf("output", f)), f));
    }

    [Fact]
    public async Task Should_WriteSpecWithSourceAndKinds_When_SampleJobIsAnalysed()
    {
        await Analyse();

        using var doc = JsonDocument.Parse(File.ReadAllText(_job.PathOf("output", "spec.json")));
        var root = doc.RootElement;
        Assert.Equal(SpecSources.Regex, root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("gate").GetProperty("ok").GetBoolean());
        Assert.Equal(
            ["check", "ccCharge", "ccCredit", "deposit"],
            root.GetProperty("spec").GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task Should_WriteAnalysisLinesWithDecisions_When_SampleJobIsAnalysed()
    {
        await Analyse();

        using var doc = JsonDocument.Parse(File.ReadAllText(_job.PathOf("output", "analysis.json")));
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(11, lines.Count);
        Assert.Equal(8, lines.Count(l => l.GetProperty("decision").GetString() == "post"));
        Assert.All(lines, l => Assert.Equal(16, l.GetProperty("requestId").GetString()!.Length));
    }

    [Fact]
    public async Task Should_WriteOneRequestPerPostableLine_When_SampleJobIsAnalysed()
    {
        var analysis = await Analyse();

        var xml = File.ReadAllText(_job.PathOf("output", "request.qbxml"));
        Assert.Equal(analysis.QbXml, xml);
        Assert.All(analysis.ToPost, t => Assert.Contains($"requestID=\"{t.RequestId}\"", xml, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_FailGate_When_RequirementNamesAnotherCompany()
    {
        var analysis = await Analyse("Some Other Company Inc");

        Assert.False(analysis.Gate.Ok);
        Assert.Contains(analysis.Gate.Errors, e => e.Contains("Some Other Company Inc", StringComparison.Ordinal));
        Assert.Empty(analysis.Lines);
        Assert.False(File.Exists(_job.PathOf("output", "analysis.json")));
        Assert.True(File.Exists(_job.PathOf("output", "spec.json")));
    }

    [Fact]
    public async Task Should_UseConfiguredCompany_When_RequirementHasPlaceholder()
    {
        EditRequirement(r => r.Replace("Tropicana Properties LLC.>", "Company.>", StringComparison.Ordinal));

        var analysis = await Analyse();

        Assert.True(analysis.Gate.Ok, string.Join("; ", analysis.Gate.Errors));
        Assert.Equal(Company, analysis.Company);
    }

    [Fact]
    public async Task Should_FailGate_When_StatedAccountHasNoStatement()
    {
        File.Delete(_job.PathOf("statements", "chase-card-7788.csv"));

        var analysis = await Analyse();

        Assert.Contains(analysis.Gate.Errors, e => e.Contains("7788", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_FailGate_When_StatementAccountIsNeitherStatedNorRegistered()
    {
        _job.Copy(_job.PathOf("statements", "chase-card-7788.csv"), Path.Combine("statements", "chase-card-9999.csv"));

        var analysis = await Analyse();

        Assert.Contains(analysis.Gate.Errors, e => e.Contains("9999", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Should_PostSameLines_When_BankStatementIsATextPdf()
    {
        var fromCsv = (await Analyse()).ToPost.Select(PostableKey).Order().ToList();
        UseBankPdf();

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.Null(pdf.HoldReason);
        Assert.Equal(StatementLlmExtractor.Layout, pdf.Layout);
        Assert.True(pdf.Reconcile!.Ok, pdf.Reconcile.Message);
        Assert.True(pdf.Reconcile.Verified);
        Assert.Equal(fromCsv, analysis.ToPost.Select(PostableKey).Order());
        var request = Assert.Single(_hermes.Requests, r => r.Task == HermesTask.Statement);
        Assert.Equal(_job.PathOf("output", "hermes"), request.AuditDir);
        Assert.Contains("Home Depot #4521 Noida", request.UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Should_PostSameLines_When_BothStatementsAreTextPdfs()
    {
        var fromCsv = (await Analyse()).Lines.Select(l => (PostableKey(l), l.Decision, l.Reason)).Order().ToList();
        UsePdf("chase-checking-4521");
        UsePdf("chase-card-7788");

        var analysis = await Analyse();

        Assert.All(analysis.Statements, s => Assert.Null(s.HoldReason));
        var card = analysis.Statements.Single(s => s.Last4 == "7788");
        Assert.Equal(SourceKind.Card, card.Kind);
        Assert.True(card.Reconcile!.Verified, card.Reconcile.Message);
        Assert.Equal(fromCsv, analysis.Lines.Select(l => (PostableKey(l), l.Decision, l.Reason)).Order());
    }

    [Fact]
    public async Task Should_HoldCardPdf_When_ItsTotalsOnlyReconcileWithTheBankSign()
    {
        UsePdf("chase-card-7788");
        // 1500.00 − 62.18 − 48.75 + 15.99 + 1500.00 = 2905.06 is what a bank-sign reading would need.
        _cardAnswer = _cardAnswer.Replace("\"closingBalance\": 94.94", "\"closingBalance\": 2905.06", StringComparison.Ordinal);

        var analysis = await Analyse();

        var card = analysis.Statements.Single(s => s.Last4 == "7788");
        Assert.Equal(HoldReasons.ReconcileFailed, card.HoldReason);
        Assert.All(analysis.Lines, l => Assert.Equal(SourceKind.Bank, l.Line.Kind));
    }

    [Fact]
    public async Task Should_WriteT2RowsFileWithLayoutTotalsAndVerifiedReconcile_When_PdfIsExtracted()
    {
        UseBankPdf();

        await Analyse();

        using var doc = JsonDocument.Parse(File.ReadAllText(_job.PathOf("output", "statements", "chase-checking-4521.pdf.rows.json")));
        var root = doc.RootElement;
        Assert.Equal("hermes-t2", root.GetProperty("layout").GetString());
        Assert.Equal("4521", root.GetProperty("last4").GetString());
        Assert.Equal("bank", root.GetProperty("kind").GetString());
        Assert.Equal(7, root.GetProperty("rows").GetArrayLength());
        Assert.True(root.GetProperty("reconcile").GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("reconcile").GetProperty("verified").GetBoolean());
        Assert.Equal(13195.87m, root.GetProperty("totals").GetProperty("openingBalance").GetDecimal());
        Assert.Equal(7, root.GetProperty("totals").GetProperty("transactionCount").GetInt32());
    }

    [Fact]
    public async Task Should_HoldPdfAndKeepOthers_When_T2TotalsDoNotReconcile()
    {
        UseBankPdf();
        _statementAnswer = _statementAnswer.Replace("\"closingBalance\": 10230.45", "\"closingBalance\": 10230.55", StringComparison.Ordinal);

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.Equal(HoldReasons.ReconcileFailed, pdf.HoldReason);
        Assert.Contains("closing is 10230.55", pdf.Reconcile!.Message, StringComparison.Ordinal);
        Assert.All(analysis.Lines, l => Assert.Equal(SourceKind.Card, l.Line.Kind));
        Assert.Equal(3, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldPdfAsNotVerifiable_When_T2AnswerHasNoOpeningOrClosingBalance()
    {
        UseBankPdf();
        _statementAnswer = _statementAnswer
            .Replace("\"openingBalance\": 13195.87", "\"openingBalance\": null", StringComparison.Ordinal)
            .Replace("\"closingBalance\": 10230.45", "\"closingBalance\": null", StringComparison.Ordinal);

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.Equal(HoldReasons.ReconcileFailed, pdf.HoldReason);
        Assert.False(pdf.Reconcile!.Verified);
        Assert.StartsWith("not-verifiable", pdf.Reconcile.Message, StringComparison.Ordinal);
        Assert.Equal(3, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldPdfAndKeepOthers_When_HermesGivesNoValidAnswer()
    {
        UseBankPdf();
        _statementAnswer = "I could not read this statement.";

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.File.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal(HoldReasons.HermesFailed, pdf.HoldReason);
        Assert.Equal("4521", pdf.Last4);
        Assert.Equal(3, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldPdf_When_ItsKindContradictsTheRequirement()
    {
        UseBankPdf();
        _statementAnswer = _statementAnswer.Replace("\"kind\": \"bank\"", "\"kind\": \"card\"", StringComparison.Ordinal);

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.NotNull(pdf.HoldReason);
        Assert.Equal(3, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldScannedPdfAndKeepOthers_When_OcrIsDisabled()
    {
        PdfBuilder.Write(_job.PathOf("statements", "chase-card-7788-extra.pdf"), PdfPageSpec.Scan(TestImage.Png()));

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.File.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal(HoldReasons.ScannedPdfOcrDisabled, pdf.HoldReason);
        Assert.Equal(8, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_SendOcrTextToHermesAndPost_When_ScannedPdfIsReadWithOcr()
    {
        var ocr = new FakeOcr().Respond(_ => Fixtures.Read("statements", "chase-checking-4521.pdf.txt"));
        _ocr = ocr;
        File.Delete(_job.PathOf("statements", "chase-checking-4521.csv"));
        PdfBuilder.Write(_job.PathOf("statements", "chase-checking-4521.pdf"), PdfPageSpec.Scan(TestImage.Png()));

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.Null(pdf.HoldReason);
        Assert.Single(ocr.Images);
        Assert.Contains("Home Depot #4521 Noida", Assert.Single(_hermes.Requests, r => r.Task == HermesTask.Statement).UserContent, StringComparison.Ordinal);
        Assert.Equal(8, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldDamagedPdfAndKeepOthers_When_PdfCannotBeOpened()
    {
        _job.WithFile(Path.Combine("statements", "chase-card-7788-extra.pdf"), "not a pdf");

        var analysis = await Analyse();

        var pdf = analysis.Statements.Single(s => s.File.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal(HoldReasons.UnreadableStatement, pdf.HoldReason);
        Assert.Equal(8, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_PostSameLines_When_BankStatementIsAWorkbook()
    {
        var fromCsv = (await Analyse()).ToPost.Select(Postable).ToList();
        File.Delete(_job.PathOf("statements", "chase-checking-4521.csv"));
        _job.Copy(Fixtures.PathOf("statements", "chase-checking-4521.xlsx"), Path.Combine("statements", "chase-checking-4521.xlsx"));

        var analysis = await Analyse();

        var xlsx = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.Null(xlsx.HoldReason);
        Assert.Equal("chase-checking", xlsx.Layout);
        Assert.True(xlsx.Reconcile!.Ok);
        Assert.Equal(fromCsv, analysis.ToPost.Select(Postable));
        Assert.True(File.Exists(_job.PathOf("output", "statements", "chase-checking-4521.xlsx.rows.json")));

        static object Postable(MappedTxn m) =>
            (m.Line.Date, m.Line.Amount, m.Line.Direction, m.Kind, m.Account, m.Payee, m.LineAccount, m.RefNumber);
    }

    [Fact]
    public async Task Should_HoldStatementAndKeepOthers_When_WorkbookIsCorrupt()
    {
        _job.WithFile(Path.Combine("statements", "chase-card-7788-extra.xlsx"), "not a workbook");

        var analysis = await Analyse();

        var xlsx = analysis.Statements.Single(s => s.File.EndsWith(".xlsx", StringComparison.Ordinal));
        Assert.Equal(HoldReasons.UnreadableStatement, xlsx.HoldReason);
        Assert.Equal("7788", xlsx.Last4);
        Assert.Equal(8, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldWholeStatement_When_BalanceChainBreaks()
    {
        var bank = _job.PathOf("statements", "chase-checking-4521.csv");
        File.WriteAllText(bank, File.ReadAllText(bank).Replace("9980.45", "9990.45", StringComparison.Ordinal));

        var analysis = await Analyse();

        var statement = analysis.Statements.Single(s => s.Last4 == "4521");
        Assert.Equal(HoldReasons.ReconcileFailed, statement.HoldReason);
        Assert.False(statement.Reconcile!.Ok);
        Assert.All(analysis.Lines, l => Assert.Equal(SourceKind.Card, l.Line.Kind));
        Assert.Equal(3, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_SkipLine_When_LedgerAlreadyHasItsFingerprint()
    {
        var first = await Analyse();
        var posted = first.ToPost[0];
        new LedgerStore(LedgerFile).Save(new Ledger
        {
            Posted =
            [
                new LedgerEntry
                {
                    BatchId = "2026-08-tropicana#1", JobId = "2026-08-tropicana", Fingerprint = posted.Line.Fingerprint,
                    TxnId = "T-1", Kind = posted.Kind, Account = posted.Account!, Amount = posted.Line.Amount,
                    Date = posted.Line.Date, SourceFile = posted.Line.SourceFile,
                },
            ],
        });

        var second = await Analyse();

        var line = second.Lines.Single(l => l.RequestId == posted.RequestId);
        Assert.Equal(Decision.Skip, line.Decision);
        Assert.Equal(HoldReasons.AlreadyPosted, line.Reason);
        Assert.Equal(7, second.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldBothLines_When_IdenticalLineAppearsTwice()
    {
        var card = _job.PathOf("statements", "chase-card-7788.csv");
        File.AppendAllText(card, "\n08/09/2026,08/10/2026,SHELL OIL 57442,Gas,Sale,-48.75,\n");

        var analysis = await Analyse();

        var shell = analysis.Lines.Where(l => l.Line.Description == "SHELL OIL 57442").ToList();
        Assert.Equal(2, shell.Count);
        Assert.All(shell, l => Assert.Equal(HoldReasons.DuplicateLine, l.Reason));
        Assert.Equal(7, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_HoldDeposits_When_RequirementDoesNotAskForThem()
    {
        EditRequirement(r => r[..r.IndexOf("Transactions Type > Deposit", StringComparison.Ordinal)]);

        var analysis = await Analyse();

        var deposit = analysis.Lines.Single(l => l.Line.Description == "DEPOSIT PALM COURT RENTALS");
        Assert.Equal(HoldReasons.KindNotRequested, deposit.Reason);
        Assert.DoesNotContain(analysis.ToPost, t => t.Kind == TxnKind.Deposit);
    }

    [Fact]
    public async Task Should_HoldStatement_When_RequirementStatesItAsTheOtherKind()
    {
        File.WriteAllText(
            _job.PathOf("requirement.txt"),
            "Tropicana Properties LLC.>Batch Enter Transactions\n\nTransactions Type => Checks (Debit Entries)\nBank Account=From Statement 4521 7788\n");

        var analysis = await Analyse();

        Assert.Equal(HoldReasons.KindMismatch, analysis.Statements.Single(s => s.Last4 == "7788").HoldReason);
    }

    [Fact]
    public async Task Should_ReturnPartialWithoutCallingQuickBooks_When_ReanalysisFailsDuringPost()
    {
        var job = Job() with { Status = JobStatus.Posting, BatchId = "2026-08-tropicana#1", Attempt = 1 };

        var outcome = await Pipeline("Some Other Company Inc").RunPostAsync(job, CancellationToken.None);

        Assert.Equal(JobStatus.Partial, outcome.Status);
        Assert.StartsWith("nothing posted", outcome.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(LedgerFile));
    }

    [Fact]
    public void Should_NumberAttemptsAfterLedgerBatches_When_PostingBegins()
    {
        new LedgerStore(LedgerFile).Save(new Ledger
        {
            Jobs = [new LedgerJob { JobId = "2026-08-tropicana", BatchId = "2026-08-tropicana#1" }],
        });

        var posting = Pipeline().BeginPosting(Job());

        Assert.Equal(JobStatus.Posting, posting.Status);
        Assert.Equal(2, posting.Attempt);
        Assert.Equal("2026-08-tropicana#2", posting.BatchId);
    }

    [Fact]
    public async Task Should_MatchSampleInvoiceToHomeDepotLine_When_SampleJobIsAnalysed()
    {
        var analysis = await Analyse();

        var invoice = Assert.Single(analysis.Invoices);
        var homeDepot = Assert.Single(analysis.Lines, l => l.Line.Description.StartsWith("HOME DEPOT", StringComparison.Ordinal));
        Assert.True(invoice.Matched);
        Assert.Equal(homeDepot.RequestId, invoice.Facts!.MatchedRequestId);
        Assert.Equal("home-depot-88213.pdf", invoice.File);
        Assert.Null(invoice.Reason);
    }

    [Fact]
    public async Task Should_SetInvoiceRefOnHomeDepotLine_When_SampleJobIsAnalysed()
    {
        var analysis = await Analyse();

        var homeDepot = Assert.Single(analysis.Lines, l => l.InvoiceRef is not null);
        Assert.Equal("home-depot-88213.pdf", homeDepot.InvoiceRef);
        Assert.Equal("Home Depot", homeDepot.Payee);
        Assert.Equal(8, analysis.ToPost.Count);
    }

    [Fact]
    public async Task Should_SupplyPayeeFromInvoice_When_LineDescriptionNamesNoVendor()
    {
        new QbListsStore(Path.Combine(_job.Root, "qb-lists.json")).Save(new QbLists { Vendors = ["Joe's Plumbing"] });
        _invoiceAnswer = """{ "party": "Joe's Plumbing", "role": "vendor", "number": "77", "date": "2026-08-14", "total": 3199.70 }""";

        var analysis = await Analyse();

        var plumber = Assert.Single(analysis.Lines, l => l.Line.Description.Contains("PLUMBER", StringComparison.Ordinal));
        Assert.Equal("Joe's Plumbing", plumber.Payee);
        Assert.Equal("home-depot-88213.pdf", plumber.InvoiceRef);
        Assert.Contains("payee from invoice", plumber.Note, StringComparison.Ordinal);
        // No account rule for the new vendor yet: still held, now for the account instead of the payee (tiers 3–4 in M5).
        Assert.Equal(HoldReasons.NoAccountRule, plumber.Reason);
    }

    [Fact]
    public async Task Should_SendInvoiceWithCompanyAndAuditFolder_When_SampleJobIsAnalysed()
    {
        await Analyse();

        var request = Assert.Single(_hermes.Requests, r => r.Task == HermesTask.Invoice);
        Assert.Contains("Our company: " + Company, request.UserContent, StringComparison.Ordinal);
        Assert.Contains("TOTAL 184.32", request.UserContent, StringComparison.Ordinal);
        Assert.Equal(_job.PathOf("output", "hermes"), request.AuditDir);
    }

    [Fact]
    public async Task Should_WriteInvoiceJson_When_InvoiceIsRead()
    {
        var analysis = await Analyse();

        var path = _job.PathOf("output", "invoices", "home-depot-88213.pdf.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.Equal("home-depot-88213.pdf", root.GetProperty("file").GetString());
        var facts = root.GetProperty("facts");
        Assert.Equal("Home Depot", facts.GetProperty("party").GetString());
        Assert.Equal("vendor", facts.GetProperty("role").GetString());
        Assert.Equal("2026-08-21", facts.GetProperty("date").GetString());
        Assert.Equal(184.32m, facts.GetProperty("total").GetDecimal());
        Assert.Equal(analysis.Invoices[0].Facts!.MatchedRequestId, facts.GetProperty("matchedRequestId").GetString());
        Assert.Single(root.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task Should_ReportUnmatchedInvoiceAndKeepDecisions_When_NoLineHasItsTotal()
    {
        _invoiceAnswer = _invoiceAnswer.Replace("184.32", "999.99", StringComparison.Ordinal);

        var analysis = await Analyse();
        var result = ResultDocument.Build(Job(), analysis, null, DateTime.UnixEpoch, DateTime.UnixEpoch);

        var unmatched = Assert.Single(result.UnmatchedInvoices);
        Assert.Equal("home-depot-88213.pdf", unmatched.File);
        Assert.Equal(HoldReasons.NoMatchingLine, unmatched.Reason);
        Assert.Null(unmatched.Facts!.MatchedRequestId);
        Assert.Equal((8, 2, 1), (analysis.ToPost.Count, result.Counts.Held, result.Counts.Skipped));
    }

    [Fact]
    public async Task Should_ReportNoUnmatchedInvoices_When_SampleInvoiceMatches()
    {
        var analysis = await Analyse();

        Assert.Empty(ResultDocument.Build(Job(), analysis, null, DateTime.UnixEpoch, DateTime.UnixEpoch).UnmatchedInvoices);
    }

    [Fact]
    public async Task Should_ReportInvoiceHermesFailedAndStillAnalyse_When_T3AnswerIsInvalid()
    {
        _invoiceAnswer = """{ "party": "Home Depot", "role": "supplier", "total": 184.32 }""";

        var analysis = await Analyse();
        var result = ResultDocument.Build(Job(), analysis, null, DateTime.UnixEpoch, DateTime.UnixEpoch);

        var unmatched = Assert.Single(result.UnmatchedInvoices);
        Assert.Equal(HoldReasons.HermesFailed, unmatched.Reason);
        Assert.Null(unmatched.Facts);
        Assert.NotEmpty(unmatched.Errors);
        Assert.Equal(8, analysis.ToPost.Count);
        Assert.True(File.Exists(_job.PathOf("output", "invoices", "home-depot-88213.pdf.json")));
    }

    [Fact]
    public async Task Should_ReportUnreadable_When_InvoiceIsAnImageAndOcrIsDisabled()
    {
        _job.Copy(Fixtures.PathOf("invoices", "home-depot-88213.pdf"), Path.Combine("invoices", "receipt.png"));

        var analysis = await Analyse();

        var image = Assert.Single(analysis.Invoices, i => i.File == "receipt.png");
        Assert.Equal(HoldReasons.Unreadable, image.Reason);
        Assert.True(Assert.Single(analysis.Invoices, i => i.File == "home-depot-88213.pdf").Matched);
        Assert.Single(_hermes.Requests, r => r.Task == HermesTask.Invoice);
    }

    [Fact]
    public async Task Should_NotMatchLinesOfHeldStatements_When_BankStatementFailsExtraction()
    {
        UseBankPdf();
        _statementAnswer = "not json";

        var analysis = await Analyse();

        Assert.True(Assert.Single(analysis.Statements, s => s.File == "chase-checking-4521.pdf").IsHeld);
        var invoice = Assert.Single(analysis.Invoices);
        Assert.Equal(HoldReasons.NoMatchingLine, invoice.Reason);
    }

    [Fact]
    public async Task Should_NotReadInvoices_When_RequirementCheckFails()
    {
        var analysis = await Analyse(company: "Another Company Inc");

        Assert.False(analysis.Gate.Ok);
        Assert.Empty(analysis.Invoices);
        Assert.DoesNotContain(_hermes.Requests, r => r.Task == HermesTask.Invoice);
        Assert.False(Directory.Exists(_job.PathOf("output", "invoices")));
    }

    [Fact]
    public async Task Should_ReadNoInvoices_When_FolderHasNone()
    {
        Directory.Delete(_job.PathOf("invoices"), recursive: true);

        var analysis = await Analyse();

        Assert.Empty(analysis.Invoices);
        Assert.Equal(8, analysis.ToPost.Count);
    }

    /// <summary>Analysis never talks to QuickBooks.</summary>
    private sealed class UnusedGateway : IQbGateway
    {
        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
            throw new InvalidOperationException("QuickBooks must not be called here.");

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
            throw new InvalidOperationException("QuickBooks must not be called here.");
    }
}
