using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Text;

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

    /// <summary>
    /// T-1210: the EF Core store's order of checks. The one lock around all state stands in for its transaction and
    /// the extraction row's <c>FOR UPDATE</c>: two saves of one extraction run one after the other, so the second sees
    /// the first's <c>SavedRequestId</c>.
    /// </summary>
    public Task<UploadSaveResult> SaveUploadAsync(NewUploadSave save, CancellationToken ct)
    {
        lock (_gate)
        {
            var request = save.Request;
            var holder = request.IdempotencyKey is null
                ? null
                : _requests.Values.FirstOrDefault(r => r.Source == request.Source && r.IdempotencyKey == request.IdempotencyKey);
            if (holder is not null && string.Equals(holder.OriginalBody, request.OriginalBody, StringComparison.Ordinal))
            {
                return Done(UploadSaveKind.Replayed, View(holder));
            }

            if (!_extractions.TryGetValue(save.ExtractionId, out var extraction) || extraction.ExpiresUtc <= request.NowUtc)
            {
                if (extraction is { SavedRequestId: null })
                {
                    _extractions.Remove(extraction.Id);
                }

                return Done(UploadSaveKind.Expired, null);
            }

            if (extraction.UserId != save.UserId)
            {
                return Done(UploadSaveKind.NotFound, null);
            }

            if (extraction.SavedRequestId is { } savedId)
            {
                return Task.FromResult(new UploadSaveResult(UploadSaveKind.AlreadySaved, null, savedId, $"SR-{savedId:000000}"));
            }

            // SPEC-GAP T-1210 (Q-111): same key, other body, extraction not saved; KeyReused as CreateAsync, nothing written.
            if (holder is not null)
            {
                return Done(UploadSaveKind.KeyReused, View(holder));
            }

            var id = _nextRequestId++;
            var upload = new UploadMeta(
                extraction.FileName, extraction.SizeBytes, extraction.Sha256, extraction.Model, extraction.PromptVersion,
                extraction.ExtractedUtc);
            var record = Insert(id, request, save.Version2Payload is null ? 1 : 2, upload);

            var read1 = save.Version2Payload is null ? request.Read : SavedPayloadReader.Read(save.Version1Payload);
            var versions = new List<SavedRequestVersion>
            {
                new(_nextVersionId++, id, 1, save.Version1Payload.Clone(), read1.Readable, read1.Errors.ToList(), [],
                    save.Version1Note, request.NowUtc, request.CreatedBy),
            };
            if (save.Version2Payload is { } version2)
            {
                var changes = save.Version2Changes is null
                    ? []
                    : JsonSerializer.Deserialize<List<PayloadChange>>(save.Version2Changes, JsonOptions.Default) ?? [];
                versions.Add(new(
                    _nextVersionId++, id, 2, version2.Clone(), request.Read.Readable, request.Read.Errors.ToList(), changes,
                    save.Version2Note, request.NowUtc, request.CreatedBy));
            }

            _versions[id] = versions;
            AddCreatedEvents(id, request, new JsonObject { ["source"] = "upload", ["fileName"] = extraction.FileName });
            _extractions[extraction.Id] = extraction with { SavedRequestId = id };
            return Done(UploadSaveKind.Created, View(record));
        }

        static Task<UploadSaveResult> Done(UploadSaveKind kind, SavedRequest? record) =>
            Task.FromResult(new UploadSaveResult(kind, record, null, null));
    }

    public Task<IReadOnlyList<UploadDuplicateCandidate>> UploadDuplicateCandidatesAsync(
        string sha256, DateTime sinceUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<UploadDuplicateCandidate> candidates = _requests.Values
                .Where(r => r.CreatedUtc >= sinceUtc || r.Upload?.Sha256 == sha256)
                .OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id)
                .Select(r => new UploadDuplicateCandidate(
                    r.Id, r.Reference, r.Status, r.Metadata.EmailReceivedUtc ?? r.CreatedUtc, r.CreatedUtc,
                    r.Metadata.HasEmail ? "email" : r.Upload is not null ? "upload" : "other",
                    r.Upload?.Sha256,
                    new HashSet<string>(_fingerprints[r.Id], StringComparer.Ordinal),
                    UploadDuplicateMatcher.RowKeysOf(r.CurrentPayload)))
                .ToList();
            return Task.FromResult(candidates);
        }
    }
}
