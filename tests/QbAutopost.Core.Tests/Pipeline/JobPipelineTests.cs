using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Output;
using QbAutopost.Core.Pipeline;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Pipeline;

/// <summary>FR-1…FR-9 on the sample job (M1: regex spec reader, CSV only, no QuickBooks call).</summary>
public sealed class JobPipelineTests : IDisposable
{
    private const string Company = "Tropicana Properties LLC";

    private readonly TempJobFolder _job = TempJobFolder.FromSample();

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
        new UnusedGateway(),
        new SystemClock());

    private JobRecord Job() => new() { JobId = "2026-08-tropicana", Folder = _job.Folder, Status = JobStatus.Analysing, DryRun = true };

    private Task<AnalysisResult> Analyse(string company = Company) => Pipeline(company).RunAnalysisAsync(Job(), CancellationToken.None);

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
    public async Task Should_HoldStatementAndKeepOthers_When_ExtensionHasNoExtractorYet()
    {
        _job.WithFile(Path.Combine("statements", "chase-card-7788-extra.xlsx"));

        var analysis = await Analyse();

        var xlsx = analysis.Statements.Single(s => s.File.EndsWith(".xlsx", StringComparison.Ordinal));
        Assert.Equal(HoldReasons.ExtractorNotAvailable, xlsx.HoldReason);
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

    /// <summary>Analysis never talks to QuickBooks.</summary>
    private sealed class UnusedGateway : IQbGateway
    {
        public Task<string> ProcessAsync(string qbxml, CancellationToken ct) =>
            throw new InvalidOperationException("QuickBooks must not be called here.");

        public Task<string> CurrentCompanyFileAsync(CancellationToken ct) =>
            throw new InvalidOperationException("QuickBooks must not be called here.");
    }
}
