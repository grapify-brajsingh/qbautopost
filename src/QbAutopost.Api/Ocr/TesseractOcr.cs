using QbAutopost.Core.Abstractions;
using Tesseract;

namespace QbAutopost.Api.Ocr;

/// <summary>
/// Tesseract OCR, used only when <c>Ocr:Enabled</c> is true (spec FR-3, §12). The engine is created once at startup,
/// so missing language data stops the host instead of failing a job. The Tesseract NuGet package ships Windows
/// native libraries; on other systems the Tesseract and Leptonica libraries must be installed.
/// </summary>
public sealed class TesseractOcr : IOcr, IDisposable
{
    public const string Language = "eng";

    private readonly TesseractEngine _engine;
    private readonly object _gate = new(); // TesseractEngine is not thread-safe.

    public TesseractOcr(string tessDataPath)
    {
        if (string.IsNullOrWhiteSpace(tessDataPath))
        {
            throw new InvalidOperationException(
                $"Ocr:Enabled is true but Ocr:TessDataPath is empty. Set it to the folder holding {Language}.traineddata.");
        }

        var model = Path.Combine(tessDataPath, Language + ".traineddata");
        if (!File.Exists(model))
        {
            throw new InvalidOperationException(
                $"Ocr:Enabled is true but '{model}' was not found. Set Ocr:TessDataPath to the folder holding {Language}.traineddata.");
        }

        _engine = new TesseractEngine(tessDataPath, Language, EngineMode.Default);
    }

    public bool Enabled => true;

    public Task<string> ReadImageAsync(byte[] image, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var pix = Pix.LoadFromMemory(image);
            using var page = _engine.Process(pix);
            return Task.FromResult(page.GetText() ?? "");
        }
    }

    public void Dispose() => _engine.Dispose();
}
