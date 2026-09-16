namespace QbAutopost.Core.Abstractions;

/// <summary>
/// OCR engine for scanned statement pages and invoice images (spec FR-3, FR-5), switched by <c>Ocr.Enabled</c>.
/// Callers check <see cref="Enabled"/> first and hold the file when it is false.
/// </summary>
public interface IOcr
{
    bool Enabled { get; }

    /// <summary>Text recognised in one encoded image (PNG or JPEG bytes).</summary>
    Task<string> ReadImageAsync(byte[] image, CancellationToken ct);
}

/// <summary>The default engine (<c>Ocr.Enabled = false</c>): never reads anything.</summary>
public sealed class DisabledOcr : IOcr
{
    public bool Enabled => false;

    public Task<string> ReadImageAsync(byte[] image, CancellationToken ct) =>
        throw new InvalidOperationException("OCR is disabled (Ocr.Enabled = false).");
}
