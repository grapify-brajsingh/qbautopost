using System.Text.Json;
using QbAutopost.Core.Abstractions;
using QbAutopost.Core.Text;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>
/// Serves canned answers from <c>tests/fixtures/hermes/&lt;task&gt;.json</c> (spec §15). A test can script answers with
/// <see cref="Respond"/>; like the real client after its retry, an answer that fails validation throws
/// <see cref="HermesValidationException"/>.
/// </summary>
public sealed class FakeHermesClient : IHermesClient
{
    private readonly List<Func<HermesRequest, string?>> _responders = [];

    public List<HermesRequest> Calls { get; } = [];

    /// <summary>What <see cref="PingAsync"/> answers; defaults to a healthy Hermes.</summary>
    public HermesPing Ping { get; set; } = new(true, "fake-model", 1, null);

    public Task<HermesPing> PingAsync(CancellationToken ct) => Task.FromResult(Ping);

    public static string FixtureJson(HermesTask task) =>
        File.ReadAllText(Fixtures.PathOf("hermes", task.ToString().ToLowerInvariant() + ".json"));

    /// <summary>
    /// Adds a responder: it returns the answer JSON for a request, null to leave the request to the next responder
    /// (and finally to the fixture), or throws to simulate a Hermes failure.
    /// </summary>
    public FakeHermesClient Respond(Func<HermesRequest, string?> responder)
    {
        lock (_responders)
        {
            _responders.Add(responder);
        }

        return this;
    }

    public Task<T> CompleteJsonAsync<T>(HermesRequest request, CancellationToken ct)
        where T : IValidatable
    {
        Func<HermesRequest, string?>[] responders;
        lock (_responders)
        {
            Calls.Add(request);
            responders = [.. _responders];
        }

        var json = responders.Select(r => r(request)).FirstOrDefault(j => j is not null) ?? FixtureJson(request.Task);
        var answer = JsonSerializer.Deserialize<T>(json, JsonOptions.Default)
                     ?? throw new InvalidDataException($"Answer for {request.Task} is empty.");
        var errors = answer.Validate();
        if (errors.Count == 0 && request.Check is not null)
        {
            errors = request.Check(answer);
        }

        return errors.Count == 0
            ? Task.FromResult(answer)
            : Task.FromException<T>(new HermesValidationException(request.Task, errors));
    }
}
