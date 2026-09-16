namespace QbAutopost.Core.Hermes;

/// <summary>Connection settings for <see cref="HermesClient"/> (spec §12 <c>Hermes</c>). <see cref="ApiKey"/> is never logged or written.</summary>
public sealed record HermesOptions
{
    public const string CompletionsPath = "/v1/chat/completions";

    public string BaseUrl { get; init; } = "http://127.0.0.1:8642";

    public string ApiKey { get; init; } = "";

    public string Model { get; init; } = "default";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public Uri CompletionsUri => new(BaseUrl.TrimEnd('/') + CompletionsPath);

    public override string ToString() => $"HermesOptions {{ BaseUrl = {BaseUrl}, Model = {Model}, Timeout = {Timeout} }}";
}
