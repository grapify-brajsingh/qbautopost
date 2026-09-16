using QbAutopost.Core.Abstractions;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Scripted OCR engine: records every image and answers with <see cref="Respond"/>'s function (default: fixed text).</summary>
internal sealed class FakeOcr(bool enabled = true) : IOcr
{
    private Func<byte[], string> _respond = _ => "OCR TEXT";

    public bool Enabled { get; } = enabled;

    public List<byte[]> Images { get; } = [];

    public FakeOcr Respond(Func<byte[], string> respond)
    {
        _respond = respond;
        return this;
    }

    public Task<string> ReadImageAsync(byte[] image, CancellationToken ct)
    {
        Images.Add(image);
        return Task.FromResult(_respond(image));
    }
}
