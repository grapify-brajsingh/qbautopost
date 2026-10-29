using System.Text.Json;
using QbAutopost.Api.SavedRequests;
using QbAutopost.Api.Security;
using QbAutopost.Core.Api;
using QbAutopost.Core.Extraction;
using QbAutopost.Core.SavedRequests;
using QbAutopost.Core.Security;

namespace QbAutopost.Api.Endpoints;

public static partial class SavedRequestEndpoints
{
    /// <summary>Dashboard-api-m12 7.2 item 1: the problem type of an unknown or expired extraction.</summary>
    public const string ExtractionExpiredType = "extraction-expired";

    /// <summary>Dashboard-api-m12 7.2 item 1: the problem type of an extraction that was saved before.</summary>
    public const string ExtractionAlreadySavedType = "extraction-already-saved";

    /// <summary>
    /// Dashboard-api-m12 7.2: a v2 save whose body carries <c>upload</c>. Before any store call: an API-key caller is
    /// <c>403</c>, <c>email</c> or <c>pushId</c> beside <c>upload</c> is <c>400 invalid-request</c>, and an
    /// <c>extractionId</c> that is not a UUID is <c>400 invalid-request</c>. The store's answer then maps to the v2
    /// save's own answers where they mean the same thing (created, replay, key reuse) and to the upload problems
    /// otherwise.
    /// </summary>
    private static async Task<IResult> SaveUploadAsync(
        HttpContext http, SavedRequestIntake intake, string raw, JsonElement payload, UploadField upload, string? key,
        Caller caller, CancellationToken ct)
    {
        if (caller.Kind != CallerKind.User)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Signed-in user required",
                detail: "An upload belongs to a signed-in dashboard user; an API key cannot save one. Nothing was saved.");
        }

        if (UploadField.HasEmailOrPushId(payload))
        {
            return BadRequest(
                "upload with email or pushId",
                "A save with upload carries neither email nor pushId: an upload has neither. Nothing was saved.");
        }

        if (upload.Kind == UploadFieldKind.Invalid)
        {
            return BadRequest("Invalid upload", "upload.extractionId must be a UUID. Nothing was saved.");
        }

        var result = await intake.SaveUploadAsync(raw, upload.ExtractionId, key, caller, ct);
        switch (result.Kind)
        {
            case UploadSaveKind.Created:
                http.Response.Headers.Location = Location(ApiFlavor.V2, result.Record!.Id);
                return Results.Json(
                    SavedRequestViewsV2.Saved(result.Record), SavedRequestJson.Options, statusCode: StatusCodes.Status201Created);
            case UploadSaveKind.Replayed:
                http.Response.Headers[IdempotencyMiddleware.ReplayedHeader] = "true";
                return Json(SavedRequestViewsV2.Saved(result.Record!));
            case UploadSaveKind.KeyReused:
                return KeyReused(result.Record!);
            case UploadSaveKind.Expired:
                return Problem(
                    StatusCodes.Status410Gone, "Extraction expired",
                    "This extraction is unknown or has expired. Upload the PDF again. Nothing was saved.",
                    ExtractionExpiredType);
            case UploadSaveKind.NotFound:
                return NotFound();
            case UploadSaveKind.AlreadySaved:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Extraction already saved",
                    detail: $"This extraction was already saved as {result.PublicRef}. Nothing new was saved.",
                    type: ExtractionAlreadySavedType,
                    extensions: new Dictionary<string, object?>
                    {
                        ["savedRequestId"] = result.SavedRequestId,
                        ["publicRef"] = result.PublicRef,
                    });
            default:
                throw new InvalidOperationException($"Unknown upload save result {result.Kind}.");
        }
    }
}
