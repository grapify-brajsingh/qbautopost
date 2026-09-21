using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Jobs;
using QbAutopost.Core.Output;

namespace QbAutopost.Api.Tests.Api;

/// <summary>
/// POC mode (T-806): <c>Hermes:Enabled=false</c> reads the requirement with the regex parser and never asks a model;
/// the <c>samples/poc</c> data posts every line by rule, and names only what its IIF list import creates in QuickBooks.
/// </summary>
public sealed class PocModeApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientWithoutHermes(string rulesFile)
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hermes:Enabled"] = "false",
                ["Company:RulesFile"] = rulesFile,
            })));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiFactory.ApiKey);
        return client;
    }

    [Fact]
    public async Task Should_PostEveryLineByRule_When_PocJobRunsWithoutHermes()
    {
        using var client = ClientWithoutHermes(Fixtures.PocRules);

        var view = await client.RunToEndAsync(_factory.Dir.CopyJob(Fixtures.PocJob, "2026-09-tropicana"), dryRun: false);

        Assert.Equal(JobStatus.Posted, view.Status);
        Assert.Equal((15, 0, 1), (view.Counts.Posted, view.Counts.Held, view.Counts.Skipped));
        Assert.All(view.Statements, s => Assert.True(s.Reconcile?.Ok, s.File));
        Assert.Empty(_factory.Hermes.Calls);
    }

    [Fact]
    public async Task Should_PostEveryLineOfTheXlsxJob_When_HermesIsDisabled()
    {
        using var client = ClientWithoutHermes(Fixtures.PocRules);

        var view = await client.RunToEndAsync(_factory.Dir.CopyJob(Fixtures.PocXlsxJob, "2026-08-tropicana-xlsx"), dryRun: false);

        Assert.Equal(JobStatus.Posted, view.Status);
        Assert.Equal((9, 0, 0), (view.Counts.Posted, view.Counts.Held, view.Counts.Skipped));
        var statement = Assert.Single(view.Statements);
        Assert.Equal("chase-checking-4521.xlsx", statement.File);
        Assert.True(statement.Reconcile?.Verified, statement.Reconcile?.Message);
        Assert.Empty(_factory.Hermes.Calls);
    }

    [Fact]
    public async Task Should_ReadRequirementWithRegexParser_When_HermesIsDisabled()
    {
        using var client = ClientWithoutHermes(Fixtures.SampleRules);
        var folder = _factory.Dir.CopySampleJob();

        var view = await client.RunToEndAsync(folder);

        Assert.Equal(JobStatus.Ready, view.Status);
        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, FolderReader.OutputDirName, JobOutputWriter.SpecFile)));
        Assert.Equal("regex", spec.RootElement.GetProperty("source").GetString());
        Assert.DoesNotContain(_factory.Hermes.Calls, c => c.Task == HermesTask.Spec);
    }

    [Fact]
    public void Should_NameOnlyListsFromTheIifImport_When_PocRulesAreRead()
    {
        var iif = File.ReadAllLines(Fixtures.PocLists).Select(l => l.Split('\t')).ToList();
        var accounts = Names(iif, "ACCNT");
        var vendors = Names(iif, "VEND");
        var customers = Names(iif, "CUST");

        using var rules = JsonDocument.Parse(
            File.ReadAllText(Fixtures.PocRules), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var r = rules.RootElement;

        Assert.All(Values(r, "BankAccounts").Concat(Values(r, "CardAccounts")), a => Assert.Contains(a, accounts));
        Assert.All(Values(r, "PayeeAliases"), v => Assert.Contains(v, vendors));
        Assert.All(Values(r, "CustomerAliases"), c => Assert.Contains(c, customers));
        Assert.All(r.GetProperty("VendorAccounts").EnumerateObject(), p =>
        {
            Assert.Contains(p.Name, vendors);
            Assert.Contains(p.Value.GetString()!, accounts);
        });
        Assert.All(
            r.GetProperty("KeywordAccounts").EnumerateArray().Concat(r.GetProperty("TransferPatterns").EnumerateArray()),
            k => Assert.Contains(k.GetProperty("Account").GetString()!, accounts));
        Assert.Contains(r.GetProperty("HoldingExpenseAccount").GetString()!, accounts);
        Assert.Contains(r.GetProperty("DepositIncomeAccount").GetString()!, accounts);
    }

    private static HashSet<string> Names(IEnumerable<string[]> iif, string type) =>
        iif.Where(f => f[0] == type).Select(f => f[1]).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> Values(JsonElement root, string section) =>
        root.GetProperty(section).EnumerateObject().Select(p => p.Value.GetString()!);
}
