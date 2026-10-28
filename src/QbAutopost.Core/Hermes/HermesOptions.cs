namespace QbAutopost.Core.Hermes;

/// <summary>Connection settings for <see cref="HermesClient"/> (M12 §14.2 <c>OpenRouter</c> section). <see cref="ApiKey"/> is never logged or written.</summary>
public sealed record HermesOptions
{
    public const string CompletionsPath = "/v1/chat/completions";

    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; init; } = "";

    public string Model { get; init; } = "~google/gemini-pro-latest";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>OpenRouter attribution header <c>HTTP-Referer</c>; sent only if set.</summary>
    public string? Referer { get; init; }

    /// <summary>OpenRouter attribution header <c>X-Title</c>; sent only if set.</summary>
    public string? Title { get; init; }

    /// <summary>A base that already ends in <c>/v1</c> (OpenRouter) gets <c>/chat/completions</c>; a bare host gets the full path.</summary>
    public Uri CompletionsUri
    {
        get
        {
            var root = BaseUrl.TrimEnd('/');
            return new(root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? root + "/chat/completions"
                : root + CompletionsPath);
        }
    }

    public override string ToString() => $"HermesOptions {{ BaseUrl = {BaseUrl}, Model = {Model}, Timeout = {Timeout} }}";
}
