using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>
/// One row of the audit feed (spec saved-requests-dashboard-v2 8.2, 8.3). The request's reference fields are not here:
/// the API view joins them by <see cref="RequestId"/>.
/// </summary>
/// <param name="Id"><c>e&lt;eventId&gt;</c>, <c>v&lt;versionId&gt;-&lt;opIndex&gt;</c> or <c>u&lt;userAuditId&gt;</c>; stable across reads.</param>
/// <param name="ActorKind">One of <see cref="AuditActorKinds"/>.</param>
/// <param name="Action">One of <see cref="AuditActions"/>.</param>
/// <param name="RowIndex">The new version's index for add and replace, the previous version's for remove.</param>
public sealed record AuditEvent(
    string Id,
    DateTime AtUtc,
    string ActorId,
    string ActorKind,
    string Action,
    long? RequestId,
    int? RowIndex,
    string? RowExternalId,
    string? Field,
    JsonNode? Old,
    JsonNode? New,
    int? VersionNumber,
    JsonNode? Detail);
