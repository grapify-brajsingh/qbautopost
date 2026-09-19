using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// <c>Hermes:Enabled=false</c> (T-806, POC without AI): every call fails as "unavailable", so each caller does what it
/// already does when Hermes is down — a PDF statement is held, an invoice is unmatched, a line no rule resolves is held.
/// Nothing is ever guessed. The requirement is read by <c>RegexSpecReader</c> instead (see <c>Program</c>).
/// </summary>
public sealed class DisabledHermesClient : IHermesClient
{
    public const string Reason = "Hermes is turned off (Hermes:Enabled=false)";

    public Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable =>
        Task.FromException<T>(new HermesUnavailableException(request.Task, Reason));

    public Task<HermesPing> PingAsync(CancellationToken ct) =>
        Task.FromResult(new HermesPing(false, "", 0, Reason));
}
