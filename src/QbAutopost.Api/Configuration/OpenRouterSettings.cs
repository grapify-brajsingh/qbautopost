using Microsoft.Extensions.Options;

namespace QbAutopost.Api.Configuration;

/// <summary>
/// M12 §14.2 <c>OpenRouter</c> section; replaces <c>Hermes</c>. <see cref="ApiKey"/> comes from the environment
/// (<c>OpenRouter__ApiKey</c>) and is never logged or written to output.
/// </summary>
public sealed class OpenRouterSettings
{
    /// <summary>False = no AI: every model call is held as "model unavailable" (as <c>Hermes:Enabled=false</c> was).</summary>
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "~google/gemini-pro-latest";

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Optional <c>HTTP-Referer</c> attribution header.</summary>
    public string? Referer { get; set; }

    /// <summary>Optional <c>X-Title</c> attribution header.</summary>
    public string? Title { get; set; }
}

/// <summary>§14.2: the key is only ever sent over TLS, unless the host is this machine.</summary>
public sealed class OpenRouterOptionsValidator : IValidateOptions<AppSettings>
{
    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        var url = options.OpenRouter.BaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return ValidateOptionsResult.Fail("OpenRouter:BaseUrl must be an absolute URL.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return ValidateOptionsResult.Success;
        }

        // The value is a URL, not a secret, but the key travels to it - so say only what must change.
        return ValidateOptionsResult.Fail("OpenRouter:BaseUrl must be https:// unless the host is loopback, so the API key is never sent in clear text.");
    }
}

/// <summary>§14.2: an old <c>Hermes</c> section is never read; one warning says so.</summary>
public static class LegacyHermesSection
{
    public static void Warn(IConfiguration config, ILogger log)
    {
        if (config.GetSection("Hermes").Exists())
        {
            log.LogWarning(
                "The Hermes configuration section is ignored; the app now uses the OpenRouter section (OpenRouter:BaseUrl, OpenRouter:Model, OpenRouter__ApiKey). Remove Hermes from your settings files.");
        }
    }
}
