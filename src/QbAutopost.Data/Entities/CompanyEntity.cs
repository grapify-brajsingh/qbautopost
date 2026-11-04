using QbAutopost.Core.Companies;

namespace QbAutopost.Data.Entities;

/// <summary>A row of <c>companies</c> (M17 spec 4.3). The jsonb columns hold serialised string arrays as text.</summary>
public sealed class CompanyEntity
{
    public int Id { get; set; }
    public string? FolderName { get; set; }

    /// <summary><c>lower(folder_name)</c>, computed by the database: folder identity is case-insensitive (ruling B10).</summary>
    public string? FolderKey { get; set; }

    public string? Code { get; set; }
    public string DisplayName { get; set; } = "";
    public string? FilePath { get; set; }
    public string CandidateFiles { get; set; } = "[]";
    public CompanyState State { get; set; }
    public CompanyAccess Access { get; set; }
    public DateTime? AccessCheckedUtc { get; set; }
    public string? AccessMessage { get; set; }
    public string? QbCompanyName { get; set; }
    public string? QbLegalName { get; set; }
    public string MatchNames { get; set; } = "[]";
    public bool IsDefault { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Application-managed concurrency token, incremented on every update.</summary>
    public int RowVersion { get; set; }
}
