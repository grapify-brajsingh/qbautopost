using QbAutopost.Core.Mapping;
using QbAutopost.Core.Models;
using QbAutopost.Core.Store;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.Mapping;

/// <summary>FR-14 rules teaching: aliases and vendor accounts written to rules.json, checked against qb-lists.json.</summary>
public sealed class RulesEditorTests : IDisposable
{
    private readonly TempJobFolder _dir = new();

    public RulesEditorTests()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "samples", "rules.json"), RulesFile);
    }

    public void Dispose() => _dir.Dispose();

    private string RulesFile => Path.Combine(_dir.Root, "rules.json");

    private string ListsFile => Path.Combine(_dir.Root, "qb-lists.json");

    private RulesEditor Editor() => new(RulesFile, ListsFile);

    private Rules Reload() => Rules.Load(RulesFile);

    private void SaveLists(string[] accounts, string[] vendors, string[] customers) =>
        new QbListsStore(ListsFile).Save(new QbLists
        {
            Accounts = accounts.Select(a => new QbAccount { Name = a, Type = "Expense" }).ToList(),
            Vendors = vendors,
            Customers = customers,
        });

    [Fact]
    public void Should_AddVendorAlias_When_Taught()
    {
        Editor().SetAlias("unknown  plumber", "Unknown Plumber LLC", AliasKind.Vendor);

        Assert.Equal("Unknown Plumber LLC", Reload().PayeeAliases["UNKNOWN PLUMBER"]);
    }

    [Fact]
    public void Should_AddCustomerAlias_When_KindIsCustomer()
    {
        Editor().SetAlias("SUNRISE PROPERTY", "Sunrise Property Mgmt", AliasKind.Customer);

        var rules = Reload();
        Assert.Equal("Sunrise Property Mgmt", rules.CustomerAliases["SUNRISE PROPERTY"]);
        Assert.False(rules.PayeeAliases.ContainsKey("SUNRISE PROPERTY"));
    }

    [Fact]
    public void Should_ReplaceAliasAndReportPrevious_When_FragmentAlreadyExists()
    {
        var change = Editor().SetAlias("home depot", "The Home Depot", AliasKind.Vendor);

        Assert.Equal(new RuleChange("PayeeAliases", "HOME DEPOT", "The Home Depot", "Home Depot"), change);
        var aliases = Reload().PayeeAliases;
        Assert.Equal("The Home Depot", aliases["HOME DEPOT"]);
        Assert.Equal(4, aliases.Count);
    }

    [Fact]
    public void Should_SetVendorAccount_When_Taught()
    {
        var change = Editor().SetVendorAccount("Unknown Plumber LLC", "Repairs and Maintenance");

        Assert.Equal(new RuleChange("VendorAccounts", "Unknown Plumber LLC", "Repairs and Maintenance", null), change);
        Assert.Equal("Repairs and Maintenance", Reload().VendorAccounts["Unknown Plumber LLC"]);
    }

    [Fact]
    public void Should_ReplaceVendorAccountIgnoringCase_When_VendorAlreadyHasOne()
    {
        var change = Editor().SetVendorAccount("AMAZON", "Supplies");

        Assert.Equal("Office Supplies", change.Previous);
        var accounts = Reload().VendorAccounts;
        Assert.Equal("Supplies", accounts["Amazon"]);
        Assert.Equal(3, accounts.Count);
    }

    [Fact]
    public void Should_KeepOtherRules_When_AliasIsWritten()
    {
        var before = Reload();

        Editor().SetAlias("ACME", "Acme Corp", AliasKind.Vendor);

        var after = Reload();
        Assert.Equal(before.CsvLayouts.Keys, after.CsvLayouts.Keys);
        Assert.Equal(before.AccountNames(), after.AccountNames());
        Assert.Equal(before.SkipPatterns, after.SkipPatterns);
        Assert.Equal(before.ModelConfidenceThreshold, after.ModelConfidenceThreshold);
    }

    [Fact]
    public void Should_LeaveNoTempFile_When_Written()
    {
        Editor().SetAlias("ACME", "Acme Corp", AliasKind.Vendor);

        Assert.Empty(Directory.GetFiles(_dir.Root, "*.tmp"));
    }

    [Theory]
    [InlineData("", "Acme Corp")]
    [InlineData("   ", "Acme Corp")]
    [InlineData("AC", "Acme Corp")]
    [InlineData("ACME", "")]
    [InlineData("ACME", "  ")]
    public void Should_RejectAlias_When_FragmentOrNameIsBlankOrTooShort(string fragment, string name)
    {
        var before = File.ReadAllText(RulesFile);

        Assert.Throws<RuleValidationException>(() => Editor().SetAlias(fragment, name, AliasKind.Vendor));
        Assert.Equal(before, File.ReadAllText(RulesFile));
    }

    [Theory]
    [InlineData("", "Utilities")]
    [InlineData("Amazon", "")]
    public void Should_RejectVendorAccount_When_VendorOrAccountIsBlank(string vendor, string account)
    {
        Assert.Throws<RuleValidationException>(() => Editor().SetVendorAccount(vendor, account));
    }

    [Fact]
    public void Should_RejectAccount_When_ListsExistAndAccountIsNotInThem()
    {
        SaveLists(["Utilities"], ["Amazon"], []);
        var before = File.ReadAllText(RulesFile);

        var ex = Assert.Throws<RuleValidationException>(() => Editor().SetVendorAccount("Amazon", "Repairs"));

        Assert.Contains("Repairs", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(RulesFile));
    }

    [Fact]
    public void Should_RejectAccountAndSuggestSpelling_When_OnlyCaseDiffers()
    {
        SaveLists(["Utilities"], ["Amazon"], []);

        var ex = Assert.Throws<RuleValidationException>(() => Editor().SetVendorAccount("Amazon", "utilities"));

        Assert.Contains("'Utilities'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_AcceptAccount_When_ListsExistAndContainIt()
    {
        SaveLists(["Utilities"], ["Amazon"], []);

        Editor().SetVendorAccount("Amazon", "Utilities");

        Assert.Equal("Utilities", Reload().VendorAccounts["Amazon"]);
    }

    [Fact]
    public void Should_RejectVendorAccount_When_ListsExistAndVendorIsNotInThem()
    {
        SaveLists(["Utilities"], ["Amazon"], []);

        Assert.Throws<RuleValidationException>(() => Editor().SetVendorAccount("Unknown Plumber LLC", "Utilities"));
    }

    [Fact]
    public void Should_RejectAlias_When_ListsExistAndNameIsNotAKnownVendor()
    {
        SaveLists([], ["Amazon"], ["Palm Court Rentals LLC"]);

        Assert.Throws<RuleValidationException>(() => Editor().SetAlias("PALM", "Palm Court Rentals LLC", AliasKind.Vendor));
    }

    [Fact]
    public void Should_AcceptCustomerAlias_When_ListsContainTheCustomer()
    {
        SaveLists([], ["Amazon"], ["Palm Court Rentals LLC"]);

        Editor().SetAlias("PALM", "Palm Court Rentals LLC", AliasKind.Customer);

        Assert.Equal("Palm Court Rentals LLC", Reload().CustomerAliases["PALM"]);
    }

    [Fact]
    public void Should_AcceptAnyName_When_ListsWereNeverSynced()
    {
        Editor().SetVendorAccount("Brand New Vendor", "Brand New Account");

        Assert.Equal("Brand New Account", Reload().VendorAccounts["Brand New Vendor"]);
    }

    [Fact]
    public void Should_Throw_When_RulesFileIsMissing()
    {
        File.Delete(RulesFile);

        Assert.Throws<FileNotFoundException>(() => Editor().SetAlias("ACME", "Acme Corp", AliasKind.Vendor));
        Assert.False(File.Exists(RulesFile));
    }

    [Fact]
    public void Should_NotRewrite_When_RulesFileIsInvalid()
    {
        File.WriteAllText(RulesFile, """{ "ModelConfidenceThreshold": 5 }""");

        Assert.Throws<InvalidDataException>(() => Editor().SetAlias("ACME", "Acme Corp", AliasKind.Vendor));
        Assert.Equal("""{ "ModelConfidenceThreshold": 5 }""", File.ReadAllText(RulesFile));
    }

    [Fact]
    public void Should_CreateSection_When_RulesFileLacksIt()
    {
        File.WriteAllText(RulesFile, "{}");

        Editor().SetAlias("ACME", "Acme Corp", AliasKind.Customer);

        Assert.Equal("Acme Corp", Reload().CustomerAliases["ACME"]);
    }

    [Fact]
    public void Should_UseExistingSectionSpelling_When_FileUsesCamelCase()
    {
        File.WriteAllText(RulesFile, """{ "vendorAccounts": { "Amazon": "Office Supplies" } }""");

        Editor().SetVendorAccount("Shell", "Automobile Expense");

        var text = File.ReadAllText(RulesFile);
        Assert.DoesNotContain("\"VendorAccounts\"", text, StringComparison.Ordinal);
        Assert.Equal(2, Reload().VendorAccounts.Count);
    }

    [Fact]
    public void Should_KeepEveryChange_When_WritesRunConcurrently()
    {
        var editor = Editor();

        Parallel.For(0, 20, i => editor.SetAlias($"VENDOR {i:00}", $"Vendor {i:00}", AliasKind.Vendor));

        Assert.Equal(24, Reload().PayeeAliases.Count);
    }
}
