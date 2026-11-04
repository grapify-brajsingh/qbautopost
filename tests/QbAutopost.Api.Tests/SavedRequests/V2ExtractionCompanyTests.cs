using System.Text.Json;
using QbAutopost.Core.Companies;

namespace QbAutopost.Api.Tests.SavedRequests;

/// <summary>
/// T-1711 (M17 spec 4.5, 5.3): the extraction's company match. The model reads the printed names; code decides. The file
/// name never reaches the model and never decides.
/// </summary>
public sealed class V2ExtractionCompanyTests : IDisposable
{
    private const string TakomaName = "TAKOMA PARK GROCERY STORE INC";
    private const string ZemenDba = "DBA ZEMEN MARKET";

    private readonly ExtractionTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private Company Seed(int id, string code, string name, CompanyState state = CompanyState.Active)
    {
        _ = _host.App.Server; // starts the host, which creates the default company first
        return _host.Factory.Companies.Add(new Company(
            id, name, code, name, $"C:\\Companies\\{name}.qbw", [], state, CompanyAccess.Approved,
            DateTime.UtcNow, null, null, null, [], false, DateTime.UtcNow, DateTime.UtcNow, 1));
    }

    private static string Reply(string? holder, string? dba)
    {
        var names = (holder is null ? "" : $"\"accountHolderName\": \"{holder}\",")
            + (dba is null ? "" : $"\"dbaName\": \"{dba}\",");
        return "{" + names + """
            "is_bank_statement": true, "reference": "Statement 2026-10",
            "transactions": [ { "externalId": "row-1", "kind": "Deposit", "date": "2026-10-03", "amount": 10.00, "memo": "x" } ] }
            """;
    }

    /// <param name="printed">The names the PDF itself prints in its header; by default the ones the model reads.</param>
    private async Task<(JsonElement Company, JsonElement Body)> UploadAsync(
        string? holder, string? dba, string fileName = "chase-aug-2026.pdf", string[]? printed = null)
    {
        _host.Handler.ReplyContent(Reply(holder, dba));
        using var client = _host.User("editor");
        var header = printed ?? new[] { holder, dba }.OfType<string>().ToArray();
        var pdf = ExtractionTestHost.Pdf(header.Concat(ExtractionTestHost.TextPage()).ToArray());
        var (response, body) = await _host.PostAsync(client, ExtractionTestHost.Upload(pdf, fileName));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (body.GetProperty("company"), body);
    }

    private static int[] Ids(JsonElement company) =>
        company.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToArray();

    [Fact]
    public async Task Should_MatchTakoma_When_HeaderPrintsTakomaParkGroceryStoreInc()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");
        Seed(12, "22", "S & A Convenience Market LLC");

        var (company, _) = await UploadAsync(TakomaName, ZemenDba);

        Assert.Equal("matched", company.GetProperty("match").GetString());
        Assert.Equal(TakomaName, company.GetProperty("printedName").GetString());
        Assert.Equal(ZemenDba, company.GetProperty("dbaName").GetString());
        Assert.Equal(11, company.GetProperty("companyId").GetInt32());
        var candidate = Assert.Single(company.GetProperty("candidates").EnumerateArray());
        Assert.Equal(11, candidate.GetProperty("id").GetInt32());
        Assert.Equal("18", candidate.GetProperty("code").GetString());
        Assert.Equal("Takoma Park Grocery Store Inc", candidate.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Should_MatchSAndA_When_HeaderPrintsSaConvenienceMarket()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");
        Seed(12, "22", "S & A Convenience Market LLC");

        var (company, _) = await UploadAsync("SA CONVENIENCE MARKET", null);

        Assert.Equal("matched", company.GetProperty("match").GetString());
        Assert.Equal(12, company.GetProperty("companyId").GetInt32());
    }

    [Fact]
    public async Task Should_ReturnNone_When_NoNameIsPrinted()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");

        var (company, _) = await UploadAsync(null, null);

        Assert.Equal("none", company.GetProperty("match").GetString());
        Assert.Equal(JsonValueKind.Null, company.GetProperty("printedName").ValueKind);
        Assert.Equal(JsonValueKind.Null, company.GetProperty("dbaName").ValueKind);
        Assert.Equal(JsonValueKind.Null, company.GetProperty("companyId").ValueKind);
        Assert.Empty(Ids(company));
    }

    [Fact]
    public async Task Should_ReturnAmbiguous_When_TwoCompaniesShareTheName()
    {
        Seed(11, "18", "Twin Market LLC");
        Seed(12, "19", "Twin Market Inc");

        var (company, _) = await UploadAsync("TWIN MARKET", null);

        Assert.Equal("ambiguous", company.GetProperty("match").GetString());
        Assert.Equal(JsonValueKind.Null, company.GetProperty("companyId").ValueKind);
        Assert.Equal([11, 12], Ids(company));
    }

    [Fact]
    public async Task Should_ReturnNone_When_OnlyADisabledCompanyHasTheName()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc", CompanyState.Disabled);

        var (company, _) = await UploadAsync(TakomaName, null);

        Assert.Equal("none", company.GetProperty("match").GetString());
        Assert.Empty(Ids(company));
    }

    [Fact]
    public async Task Should_IgnoreTheFileName_When_HeaderHasNoName()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");

        var (company, _) = await UploadAsync(null, null, "Bank statement 18 ; Takoma park grocery store inc.pdf");

        Assert.Equal("none", company.GetProperty("match").GetString());
        Assert.DoesNotContain("Takoma park grocery store inc", _host.Handler.Requests.Single().Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Should_MatchByPrintedNameNotFileName_When_FileNameNamesAnotherCompany()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");
        Seed(12, "22", "S & A Convenience Market LLC");

        var (company, _) = await UploadAsync(TakomaName, null, "Statement ; S & A CONVENIENCE MARKET LLC.pdf");

        Assert.Equal(11, company.GetProperty("companyId").GetInt32());
    }

    [Fact]
    public async Task Should_StoreTheMatchOnTheExtraction_When_Extracted()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");

        var (company, body) = await UploadAsync(TakomaName, ZemenDba);

        var stored = await _host.Store.GetExtractionAsync(Guid.Parse(body.GetProperty("extractionId").GetString()!), default);
        Assert.NotNull(stored?.Company);
        Assert.Equal(CompanyMatchKind.Matched, stored.Company.Match);
        Assert.Equal(11, stored.Company.CompanyId);
        Assert.Equal(TakomaName, stored.Company.PrintedName);
        Assert.Equal(company.GetProperty("companyId").GetInt32(), stored.Company.CompanyId);
    }

    [Fact]
    public async Task Should_KeepPrintedNamesOutOfThePayload_When_Extracted()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");

        var (_, body) = await UploadAsync(TakomaName, ZemenDba);

        var payload = body.GetProperty("payload");
        Assert.False(payload.TryGetProperty("accountHolderName", out _));
        Assert.False(payload.TryGetProperty("dbaName", out _));
    }

    // ---- T-1711 ruling: a name the PDF does not print never matches --------------------------------------------------

    [Fact]
    public async Task Should_Match_When_ThePdfPrintsTheNameInAnotherSpelling()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");

        var (company, _) = await UploadAsync(TakomaName, null, printed: ["Takoma Park Grocery Store, Inc."]);

        Assert.Equal(("matched", 11), (company.GetProperty("match").GetString(), company.GetProperty("companyId").GetInt32()));
    }

    [Fact]
    public async Task Should_ReturnNone_When_TheModelsNameIsNotInThePdfText()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");

        var (company, _) = await UploadAsync(TakomaName, null, printed: []);

        Assert.Equal("none", company.GetProperty("match").GetString());
        Assert.Equal(JsonValueKind.Null, company.GetProperty("companyId").ValueKind);
        Assert.Empty(Ids(company));
        Assert.Equal(TakomaName, company.GetProperty("printedName").GetString());
    }

    [Fact]
    public async Task Should_MatchOnlyByTheNameInThePdf_When_TheModelAlsoReturnsAnUnprintedDba()
    {
        Seed(11, "18", "Takoma Park Grocery Store Inc");
        Seed(12, "22", "Zemen Market");

        var (company, _) = await UploadAsync(TakomaName, ZemenDba, printed: [TakomaName]);

        Assert.Equal(("matched", 11), (company.GetProperty("match").GetString(), company.GetProperty("companyId").GetInt32()));
        Assert.Equal(ZemenDba, company.GetProperty("dbaName").GetString());
    }

    [Fact]
    public async Task Should_StillReturnTheBlock_When_NoCompanyHasThePrintedName()
    {
        var (company, _) = await UploadAsync("UNKNOWN BUSINESS", null);

        Assert.Equal("none", company.GetProperty("match").GetString());
        Assert.Equal("UNKNOWN BUSINESS", company.GetProperty("printedName").GetString());
    }
}
