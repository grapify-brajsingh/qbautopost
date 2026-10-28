using System.Text.Json;
using System.Text.Json.Nodes;
using QbAutopost.Core.SavedRequests;

namespace QbAutopost.Core.Extraction;

/// <summary>
/// A save of a reviewed upload (dashboard-api-m12 7.2). <see cref="NewSavedRequest.Payload"/> of <see cref="Request"/>
/// is the <b>current</b> version: <see cref="Version2Payload"/> when present, otherwise <see cref="Version1Payload"/>;
/// its read, hash, fingerprints, snapshots and duplicate result are all computed on it. Both versions are written with
/// <see cref="NewSavedRequest.CreatedBy"/> and <see cref="NewSavedRequest.NowUtc"/>; <see cref="UserId"/> is the
/// uploading user the extraction record must belong to.
/// </summary>
public sealed record NewUploadSave(
    NewSavedRequest Request,
    Guid ExtractionId,
    string UserId,
    JsonElement Version1Payload,
    JsonElement? Version2Payload,
    JsonArray? Version2Changes,
    string Version1Note,
    string Version2Note);

/// <summary>
/// <see cref="KeyReused"/>: the idempotency key is held by a request with a different original body and the extraction
/// is not saved yet (the mirror of <see cref="CreateKind.KeyReused"/>); the result carries that request.
/// </summary>
public enum UploadSaveKind { Created, Replayed, Expired, NotFound, AlreadySaved, KeyReused }

/// <summary>
/// <see cref="Record"/> for <c>Created</c>, <c>Replayed</c> and <c>KeyReused</c>; <see cref="SavedRequestId"/> and
/// <see cref="PublicRef"/> for <c>AlreadySaved</c> (the request the extraction was saved into).
/// </summary>
public sealed record UploadSaveResult(UploadSaveKind Kind, SavedRequest? Record, long? SavedRequestId, string? PublicRef);
