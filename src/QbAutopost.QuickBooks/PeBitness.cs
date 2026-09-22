namespace QbAutopost.QuickBooks;

/// <summary>
/// Reads the machine type out of a Windows executable's PE header, which is how the bitness of a *running QuickBooks*
/// is learned without loading it (FR-A-3). Plain file reading, no COM, so it runs and is tested anywhere.
/// </summary>
public static class PeBitness
{
    public const string X64 = "x64";
    public const string X86 = "x86";
    public const string Arm64 = "arm64";

    private const int PeOffsetLocation = 0x3C;
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    /// <summary>The bitness of <paramref name="path"/>, or null when it cannot be read or is not a PE file.</summary>
    public static string? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream);
            if (stream.Length < PeOffsetLocation + 4 || reader.ReadUInt16() != 0x5A4D)
            {
                // No "MZ": not a Windows executable.
                return null;
            }

            stream.Position = PeOffsetLocation;
            var peHeader = reader.ReadInt32();
            if (peHeader <= 0 || peHeader + 6 > stream.Length)
            {
                return null;
            }

            stream.Position = peHeader;
            if (reader.ReadUInt32() != 0x0000_4550)
            {
                // No "PE\0\0" where the DOS header points: not a PE image.
                return null;
            }

            return reader.ReadUInt16() switch
            {
                MachineAmd64 => X64,
                MachineI386 => X86,
                MachineArm64 => Arm64,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A health probe reports "unknown"; it never fails because a file was locked.
            return null;
        }
    }
}
