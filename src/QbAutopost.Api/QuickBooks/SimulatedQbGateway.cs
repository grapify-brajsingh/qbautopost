using System.Collections.Concurrent;
using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary>
/// <c>QuickBooks:Fake=true</c> (Development only): answers from an in-memory <see cref="SimulatedQuickBooks"/>, one per
/// company file (M17 §4.7). The configured default file is the seeded <paramref name="company"/>.
/// </summary>
public sealed class SimulatedQbGateway(SimulatedQuickBooks company, string defaultCompanyFile = SimulatedQbGateway.CompanyFile) : IQbGateway
{
    public const string CompanyFile = @"C:\simulated\Company.QBW";

    private readonly ConcurrentDictionary<string, SimulatedQuickBooks> _companies =
        new([new KeyValuePair<string, SimulatedQuickBooks>(defaultCompanyFile, company)], StringComparer.OrdinalIgnoreCase);

    public SimulatedQuickBooks Company { get; } = company;

    public string DefaultCompanyFile { get; } = defaultCompanyFile;

    /// <summary>The simulated company behind <paramref name="file"/>; an empty one the first time another file is asked for.</summary>
    public SimulatedQuickBooks CompanyFor(string file) => _companies.GetOrAdd(file, _ => new SimulatedQuickBooks());

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => Task.FromResult(Company.Process(qbxml));

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult(DefaultCompanyFile);

    public Task<string> ProcessAsync(QbTarget target, string qbxml, CancellationToken ct) =>
        Task.FromResult(CompanyFor(target.CompanyFile).Process(qbxml));

    public Task<string> CurrentCompanyFileAsync(QbTarget target, CancellationToken ct) => Task.FromResult(target.CompanyFile);
}
