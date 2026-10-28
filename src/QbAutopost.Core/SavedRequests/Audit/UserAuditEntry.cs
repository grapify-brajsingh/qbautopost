using System.Text.Json.Nodes;

namespace QbAutopost.Core.SavedRequests.Audit;

/// <summary>A <c>user_audit</c> row as the audit feed reads it.</summary>
/// <param name="UserId">The subject; null for a failed sign-in naming an email nobody holds.</param>
public sealed record UserAuditEntry(long Id, string? UserId, string Actor, string Kind, JsonNode? Detail, DateTime AtUtc);
