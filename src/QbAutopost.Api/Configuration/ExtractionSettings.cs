namespace QbAutopost.Api.Configuration;

/// <summary>
/// Dashboard-api-m12 8: the <c>Extraction</c> section of the PDF upload (<c>POST /api/v2/statement-extractions</c>). The
/// connection and the key are <see cref="OpenRouterSettings"/>; the prompt version lives in the prompt file (6.3), not here.
/// </summary>
public sealed class ExtractionSettings
{
    /// <summary>Off until the server is configured. Also needs <c>OpenRouter:Enabled</c> and a key, else 503.</summary>
    public bool Enabled { get; set; }

    /// <summary>The model for uploads; may differ from <c>OpenRouter:Model</c>, the model for jobs.</summary>
    public string Model { get; set; } = "~google/gemini-pro-latest";

    public int TimeoutSeconds { get; set; } = 120;

    public long MaxBytes { get; set; } = 10485760;

    public int MaxPages { get; set; } = 50;

    public int MaxPerUserPerHour { get; set; } = 20;

    public int RecordHours { get; set; } = 24;
}
