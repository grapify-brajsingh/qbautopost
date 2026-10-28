using QbAutopost.Core.Extraction;

namespace QbAutopost.Api.Tests.TestSupport;

/// <summary>T-1209: the extraction records of the fake, with the semantics of the EF Core store.</summary>
public sealed partial class FakeSavedRequestStore : IUploadStore
{
    private readonly Dictionary<Guid, StatementExtraction> _extractions = new();

    public Task CreateExtractionAsync(StatementExtraction extraction, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_extractions.TryAdd(extraction.Id, extraction with { Payload = (System.Text.Json.Nodes.JsonObject)extraction.Payload.DeepClone() }))
            {
                throw new InvalidOperationException($"Extraction {extraction.Id} already exists.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredExtractionsAsync(DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            var expired = _extractions.Values.Where(e => e.SavedRequestId is null && e.ExpiresUtc <= nowUtc).Select(e => e.Id).ToList();
            expired.ForEach(id => _extractions.Remove(id));
            return Task.FromResult(expired.Count);
        }
    }

    public Task<StatementExtraction?> GetExtractionAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _extractions.TryGetValue(id, out var e)
                    ? e with { Payload = (System.Text.Json.Nodes.JsonObject)e.Payload.DeepClone() }
                    : null);
        }
    }
}
