using System.Globalization;
using Microsoft.Extensions.Options;
using QbAutopost.Api.Configuration;
using QbAutopost.Core.Store;

namespace QbAutopost.Api.Companies;

/// <summary>Where a scan-created company keeps its own rules and ledger (spec §4.4). The default company does not use this.</summary>
public sealed class CompanyDataFolder(IOptions<AppSettings> settings)
{
    private CompaniesSettings Companies => settings.Value.Companies;

    public string FolderOf(int companyId) => Path.Combine(Companies.DataFolder, companyId.ToString(CultureInfo.InvariantCulture));

    public string RulesFileOf(int companyId) => Path.Combine(FolderOf(companyId), "rules.json");

    public string LedgerFileOf(int companyId) => Path.Combine(FolderOf(companyId), "ledger.json");

    /// <summary>Creates the folder; copies the rules template to rules.json and writes an empty ledger, each only if absent.</summary>
    public void Prepare(int companyId)
    {
        Directory.CreateDirectory(FolderOf(companyId));
        var rules = RulesFileOf(companyId);
        if (!File.Exists(rules))
        {
            File.Copy(Companies.RulesTemplate, rules);
        }

        var ledger = LedgerFileOf(companyId);
        if (!File.Exists(ledger))
        {
            new LedgerStore(ledger).Save(Ledger.Empty);
        }
    }
}
