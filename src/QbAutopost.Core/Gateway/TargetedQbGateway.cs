using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Gateway;

/// <summary>
/// M17 §4.7: a view of a gateway whose untargeted calls go to one company file, so code written against the untargeted
/// <see cref="IQbGateway"/> (list sync, G4 queries, posting) works for any company through the same gateway and lock.
/// </summary>
public sealed class TargetedQbGateway(IQbGateway inner, QbTarget target) : IQbGateway
{
    public QbTarget Target => target;

    public Task<string> ProcessAsync(string qbxml, CancellationToken ct) => inner.ProcessAsync(target, qbxml, ct);

    public Task<string> CurrentCompanyFileAsync(CancellationToken ct) => inner.CurrentCompanyFileAsync(target, ct);

    public Task<string> ProcessAsync(QbTarget other, string qbxml, CancellationToken ct) => inner.ProcessAsync(other, qbxml, ct);

    public Task<string> CurrentCompanyFileAsync(QbTarget other, CancellationToken ct) => inner.CurrentCompanyFileAsync(other, ct);
}

public static class QbGatewayTargets
{
    /// <summary>A gateway whose untargeted calls go to <paramref name="target"/>; the gateway itself when target is null.</summary>
    public static IQbGateway For(this IQbGateway gateway, QbTarget? target) =>
        target is null ? gateway : new TargetedQbGateway(gateway, target);
}
