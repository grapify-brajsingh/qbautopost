using QbAutopost.Core.Jobs;
using QbAutopost.Core.Models;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Jobs;

public sealed class FolderReaderTests
{
    private static TempJobFolder ValidFolder() =>
        new TempJobFolder()
            .WithFile(FolderReader.RequirementFile, "Company.>Batch Enter Transactions")
            .WithFile("statements/bank-4521.csv");

    [Fact]
    public void Should_ReturnNoErrors_When_FolderMeetsContract()
    {
        using var job = ValidFolder();

        Assert.Empty(FolderReader.Validate(job.Folder));
    }

    [Fact]
    public void Should_ReturnNoErrors_When_SampleJobIsValidated()
    {
        using var job = TempJobFolder.FromSample();

        Assert.Empty(FolderReader.Validate(job.Folder));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_RejectFolder_When_PathIsBlank(string? folder)
    {
        Assert.NotEmpty(FolderReader.Validate(folder));
    }

    [Fact]
    public void Should_RejectFolder_When_PathIsRelative()
    {
        var errors = FolderReader.Validate(Path.Combine("jobs", "2026-08-test"));

        Assert.Contains(errors, e => e.Contains("absolute", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectFolder_When_DirectoryDoesNotExist()
    {
        using var job = new TempJobFolder();

        var errors = FolderReader.Validate(Path.Combine(job.Root, "missing"));

        Assert.Contains(errors, e => e.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectFolder_When_RequirementIsMissing()
    {
        using var job = new TempJobFolder().WithFile("statements/bank-4521.csv");

        var errors = FolderReader.Validate(job.Folder);

        Assert.Contains(errors, e => e.Contains(FolderReader.RequirementFile, StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectFolder_When_StatementsFolderIsMissing()
    {
        using var job = new TempJobFolder().WithFile(FolderReader.RequirementFile);

        var errors = FolderReader.Validate(job.Folder);

        Assert.Contains(errors, e => e.Contains("statements", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_RejectFolder_When_StatementsFolderHasNoFiles()
    {
        using var job = new TempJobFolder()
            .WithFile(FolderReader.RequirementFile)
            .WithDirectory("statements/archive");

        var errors = FolderReader.Validate(job.Folder);

        Assert.Contains(errors, e => e.Contains("empty", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026 08 tropicana")]
    [InlineData("tropicana#2")]
    public void Should_RejectFolder_When_FolderNameIsNotAValidJobId(string name)
    {
        using var job = new TempJobFolder(name)
            .WithFile(FolderReader.RequirementFile)
            .WithFile("statements/bank-4521.csv");

        var errors = FolderReader.Validate(job.Folder);

        Assert.Contains(errors, e => e.Contains("job id", StringComparison.Ordinal));
    }

    [Fact]
    public void Should_UseFolderNameAsJobId_When_PathHasTrailingSeparator()
    {
        using var job = ValidFolder();

        var input = FolderReader.Read(job.Folder + Path.DirectorySeparatorChar);

        Assert.Equal("2026-08-test", input.JobId);
    }

    [Fact]
    public void Should_ListSupportedStatementsInNameOrder_When_Read()
    {
        using var job = ValidFolder()
            .WithFile("statements/card-7788.XLSX")
            .WithFile("statements/august.pdf");

        var input = FolderReader.Read(job.Folder);

        Assert.Equal(["august.pdf", "bank-4521.csv", "card-7788.XLSX"], input.Statements.Select(s => s.FileName));
    }

    [Fact]
    public void Should_ReportUnsupportedStatement_When_ExtensionIsNotCsvXlsxPdf()
    {
        using var job = ValidFolder().WithFile("statements/notes.docx");

        var input = FolderReader.Read(job.Folder);

        var unreadable = Assert.Single(input.Unreadable);
        Assert.Equal("statements/notes.docx", unreadable.File);
        Assert.Equal(HoldReasons.UnsupportedExtension, unreadable.Reason);
        Assert.Single(input.Statements);
    }

    [Fact]
    public void Should_ReportSubfolder_When_StatementsFolderContainsOne()
    {
        using var job = ValidFolder().WithFile("statements/old/bank-4521.csv");

        var input = FolderReader.Read(job.Folder);

        var unreadable = Assert.Single(input.Unreadable);
        Assert.Equal("statements/old", unreadable.File);
        Assert.Equal(HoldReasons.SubfolderIgnored, unreadable.Reason);
    }

    [Fact]
    public void Should_ReadLast4FromFileName_When_NameHasOneFourDigitGroup()
    {
        using var job = ValidFolder().WithFile("statements/2026-chase-7788.csv");

        var input = FolderReader.Read(job.Folder);

        Assert.Equal("4521", input.Statements.Single(s => s.FileName == "bank-4521.csv").Last4FromName);
        Assert.Null(input.Statements.Single(s => s.FileName == "2026-chase-7788.csv").Last4FromName);
    }

    [Fact]
    public void Should_ListInvoicesAndReportUnsupportedOnes_When_InvoicesFolderExists()
    {
        using var job = ValidFolder()
            .WithFile("invoices/home-depot.pdf")
            .WithFile("invoices/receipt.jpg")
            .WithFile("invoices/scan.tiff");

        var input = FolderReader.Read(job.Folder);

        Assert.Equal(["home-depot.pdf", "receipt.jpg"], input.Invoices.Select(i => i.FileName));
        Assert.Equal("invoices/scan.tiff", Assert.Single(input.Unreadable).File);
    }

    [Fact]
    public void Should_ReturnNoInvoices_When_InvoicesFolderIsMissing()
    {
        using var job = ValidFolder();

        Assert.Empty(FolderReader.Read(job.Folder).Invoices);
    }

    [Fact]
    public void Should_CreateOutputFolder_When_Read()
    {
        using var job = ValidFolder();

        var input = FolderReader.Read(job.Folder);

        Assert.Equal(job.PathOf("output"), input.OutputDir);
        Assert.True(Directory.Exists(input.OutputDir));
    }

    [Fact]
    public void Should_NeverListOutputFiles_When_OutputFolderHasContent()
    {
        using var job = ValidFolder()
            .WithFile("output/statements/bank-4521.csv.rows.json", "{}")
            .WithFile("output/batch-enter-checks.csv");

        var input = FolderReader.Read(job.Folder);

        Assert.DoesNotContain(input.Statements, s => s.Path.Contains("output", StringComparison.Ordinal));
        Assert.Empty(input.Unreadable);
    }

    [Fact]
    public void Should_ThrowWithErrors_When_ReadingInvalidFolder()
    {
        using var job = new TempJobFolder();

        var ex = Assert.Throws<InvalidJobFolderException>(() => FolderReader.Read(job.Folder));

        Assert.NotEmpty(ex.Errors);
    }
}
