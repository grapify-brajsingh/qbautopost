using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Hermes;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>
/// Answers every call with the JSON the script returns (or the exception it throws); validates like the real client
/// after its retry, so an invalid answer throws <see cref="HermesValidationException"/>.
/// </summary>
internal sealed class ScriptedHermes(Func<HermesRequest, string> script) : IHermesClient
{
    public List<HermesRequest> Requests { get; } = [];

    public Task<HermesPing> PingAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable
    {
        Requests.Add(request);
        var answer = JsonReply.TryParse<T>(script(request), request.Check, out var errors);
        return answer is not null
            ? Task.FromResult(answer)
            : Task.FromException<T>(new HermesValidationException(request.Task, errors));
    }
}
