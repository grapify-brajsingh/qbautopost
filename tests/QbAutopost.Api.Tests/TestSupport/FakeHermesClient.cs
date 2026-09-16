using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>Serves canned answers from <c>tests/fixtures/hermes/&lt;task&gt;.json</c> (spec §15).</summary>
public sealed class FakeHermesClient : IHermesClient
{
    public List<HermesRequest> Calls { get; } = [];

    public Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable
    {
        Calls.Add(request);
        var task = request.Task;
        var json = File.ReadAllText(Fixtures.PathOf("hermes", task.ToString().ToLowerInvariant() + ".json"));
        var answer = JsonSerializer.Deserialize<T>(json, JsonOptions.Default)
                     ?? throw new InvalidDataException($"Fixture for {task} is empty.");
        return Task.FromResult(answer);
    }
}
