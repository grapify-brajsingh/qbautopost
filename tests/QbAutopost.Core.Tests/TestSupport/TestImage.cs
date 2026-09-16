using System.IO.Compression;
using System.Text;

namespace QbAutopost.Core.Tests.TestSupport;

/// <summary>Tiny generated images for PDF tests (no binary fixtures needed).</summary>
internal sealed record TestImage(byte[] Bytes, bool IsPng)
{
    public static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A valid white RGB PNG (8-bit, no interlace).</summary>
    public static TestImage Png(int width = 4, int height = 4)
    {
        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (width * 3)];
            Array.Fill(row, (byte)0xFF);
            row[0] = 0; // filter: none
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write(PngSignature);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", pixels.ToArray());
        WriteChunk(png, "IEND", []);
        return new TestImage(png.ToArray(), IsPng: true);
    }

    /// <summary>
    /// A JPEG header (SOI, baseline SOF0 for a 1×1 RGB image, EOI). PdfPig embeds a JPEG by reading only its
    /// header, and the fake OCR never decodes it, so no scan data is needed.
    /// </summary>
    public static TestImage Jpeg() => new(
        [
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03,
            0x01, 0x11, 0x00, 0x02, 0x11, 0x00, 0x03, 0x11, 0x00,
            0xFF, 0xD9,
        ],
        IsPng: false);

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }
}
