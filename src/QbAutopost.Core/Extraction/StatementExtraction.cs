using System.Text.Json.Nodes;

namespace QbAutopost.Core.Extraction;

/// <summary>
/// An extraction record (dashboard-api-m12 6.6): what the model read from one uploaded statement PDF, kept until it
/// expires or is saved into a request. <see cref="SavedRequestId"/> is set at save.
/// </summary>
public sealed record StatementExtraction(
    Guid Id,
    string UserId,
    string FileName,
    long SizeBytes,
    string Sha256,
    int PageCount,
    string Model,
    string PromptVersion,
    JsonObject Payload,
    DateTime ExtractedUtc,
    DateTime ExpiresUtc,
    long? SavedRequestId);
