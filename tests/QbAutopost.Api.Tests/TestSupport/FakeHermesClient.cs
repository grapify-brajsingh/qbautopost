using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Serves canned answers from <c>tests/fixtures/hermes/&lt;task&gt;.json</c> (spec §15). Not called by the M1 pipeline;
/// Hermes tasks arrive in M2–M5.
/// </summary>
public sealed class FakeHermesClient : IHermesClient
{
    public List<HermesTask> Calls { get; } = [];

    public Task<T> CompleteJsonAsync<T>(HermesTask task, string userContent, CancellationToken ct)
        where T : IValidatable
    {
        Calls.Add(task);
        var json = File.ReadAllText(Fixtures.PathOf("hermes", task.ToString().ToLowerInvariant() + ".json"));
        var answer = JsonSerializer.Deserialize<T>(json, JsonOptions.Default)
                     ?? throw new InvalidDataException($"Fixture for {task} is empty.");
        return Task.FromResult(answer);
    }
}
