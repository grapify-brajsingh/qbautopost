using QbAutopost.Core.QbXml;
using QbAutopost.Core.Tests.TestSupport;

namespace QbAutopost.Core.Tests.QbXml;

/// <summary>FR-15 list queries: request golden file and response reading.</summary>
public sealed class QbListQueryTests
{
    private static readonly DateTime Synced = new(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc);

    private static string Response(string accounts, string vendors = "statusCode=\"1\"", string customers = "statusCode=\"1\"") =>
        $"""
         <QBXML><QBXMLMsgsRs>
           <AccountQueryRs requestID="accounts" {accounts} />
           <VendorQueryRs requestID="vendors" {vendors} />
           <CustomerQueryRs requestID="customers" {customers} />
         </QBXMLMsgsRs></QBXML>
         """;

    [Fact]
    public void Should_MatchGolden_When_BuildingListQuery()
    {
        Assert.Equal(
            Fixtures.Read("qbxml", "list-query.golden.xml").ReplaceLineEndings("\n").TrimEnd(),
            QbListQuery.Build().ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Should_ReadAccountFullNamesAndTypes_When_ParsingResponse()
    {
        var lists = QbListQuery.Parse(Fixtures.Read("qbxml", "list-response.xml"), Synced);

        Assert.Equal(["Chase Checking 4521", "Utilities:Electric", "Repairs and Maintenance"], lists.Accounts.Select(a => a.Name));
        Assert.Equal(["Bank", "Expense", "Expense"], lists.Accounts.Select(a => a.Type));
    }

    [Fact]
    public void Should_ReadVendorNamesUnescaped_When_ParsingResponse()
    {
        var lists = QbListQuery.Parse(Fixtures.Read("qbxml", "list-response.xml"), Synced);

        Assert.Equal(["Home Depot", "Florida Power & Light"], lists.Vendors);
    }

    [Fact]
    public void Should_ReturnEmptyList_When_QueryFindsNothing()
    {
        var lists = QbListQuery.Parse(Fixtures.Read("qbxml", "list-response.xml"), Synced);

        Assert.Empty(lists.Customers);
        Assert.Equal(Synced, lists.SyncedUtc);
    }

    [Fact]
    public void Should_UseCustomerFullName_When_CustomerIsAJob()
    {
        const string xml = """
            <QBXML><QBXMLMsgsRs>
              <AccountQueryRs statusCode="1" />
              <VendorQueryRs statusCode="1" />
              <CustomerQueryRs statusCode="0">
                <CustomerRet><Name>Unit 4</Name><FullName>Palm Court Rentals LLC:Unit 4</FullName></CustomerRet>
              </CustomerQueryRs>
            </QBXMLMsgsRs></QBXML>
            """;

        var lists = QbListQuery.Parse(xml, Synced);

        Assert.Equal(["Palm Court Rentals LLC:Unit 4"], lists.Customers);
    }

    [Fact]
    public void Should_Throw_When_AQueryIsRefused()
    {
        var xml = Response("statusCode=\"3200\" statusMessage=\"The provided edit sequence is out-of-date.\"");

        var ex = Assert.Throws<QbStatusException>(() => QbListQuery.Parse(xml, Synced));

        Assert.Contains("AccountQueryRs failed: 3200", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Should_Throw_When_AResponseIsMissing()
    {
        const string xml = "<QBXML><QBXMLMsgsRs><AccountQueryRs statusCode=\"1\" /></QBXMLMsgsRs></QBXML>";

        Assert.Throws<QbStatusException>(() => QbListQuery.Parse(xml, Synced));
    }
}
