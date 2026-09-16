using QbAutopost.Core.Abstractions;

namespace QbAutopost.Api.QuickBooks;

/// <summary><c>QuickBooks:Fake=true</c> (Development only): answers from an in-memory <see cref="SimulatedQuickBooks"/>.</summary>
public sealed class SimulatedQbGateway(SimulatedQuickBooks company) : IQbGateway
{
    public const string CompanyFile = @"C:\simulated\Company.QBW";

    public SimulatedQuickBooks Company { get; } = company;

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => Task.FromResult(Company.Process(qbxml));

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => Task.FromResult(CompanyFile);
}
