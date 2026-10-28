using System.Text.Json.Nodes;

namespace QbAutopost.Core.Hermes;

/// <summary>
/// M12: a JSON schema the provider must follow (<c>response_format: { type: "json_schema", ... }</c>). The schema is
/// built by the caller; the client only carries it.
/// </summary>
public sealed record ResponseFormat(string Name, JsonObject Schema, bool Strict);
