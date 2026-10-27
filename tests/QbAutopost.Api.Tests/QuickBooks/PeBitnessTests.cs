using QbAutopost.Api.Tests.TestSupport;
using QbAutopost.QuickBooks;

namespace QbAutopost.Api.Tests.QuickBooks;

/// <summary>
/// T-903: how the bitness of a running QuickBooks is learned (FR-A-3). Pure PE-header reading, so the case that
/// matters on the server — an x64 QuickBooks beside an x86 host (T-609) — is testable here, on any OS, from crafted
/// bytes rather than a real executable.
/// </summary>
public sealed class PeBitnessTests : IDisposable
{
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>The smallest thing that parses: "MZ", the PE offset at 0x3C, then "PE\0\0" and the machine type.</summary>
    private string WritePe(ushort machine, string name = "fake.exe", bool mz = true, bool pe = true)
    {
        var bytes = new byte[0x50];
        if (mz)
        {
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
        }

        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);
        if (pe)
        {
            bytes[0x40] = (byte)'P';
            bytes[0x41] = (byte)'E';
        }

        BitConverter.GetBytes(machine).CopyTo(bytes, 0x44);
        var path = _dir.Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Should_ReadX64_When_MachineIsAmd64() =>
        Assert.Equal(PeBitness.X64, PeBitness.Read(WritePe(MachineAmd64)));

    [Fact]
    public void Should_ReadX86_When_MachineIsI386() =>
        Assert.Equal(PeBitness.X86, PeBitness.Read(WritePe(MachineI386)));

    [Fact]
    public void Should_ReadArm64_When_MachineIsArm64() =>
        Assert.Equal(PeBitness.Arm64, PeBitness.Read(WritePe(MachineArm64)));

    [Fact]
    public void Should_ReturnNull_When_MachineIsUnknown() =>
        Assert.Null(PeBitness.Read(WritePe(0x1234)));

    [Fact]
    public void Should_ReturnNull_When_FileIsNotAnExecutable() =>
        Assert.Null(PeBitness.Read(WritePe(MachineAmd64, "not-mz.exe", mz: false)));

    [Fact]
    public void Should_ReturnNull_When_PeSignatureIsMissing() =>
        Assert.Null(PeBitness.Read(WritePe(MachineAmd64, "no-pe.exe", pe: false)));

    [Fact]
    public void Should_ReturnNull_When_FileIsTooShort()
    {
        var path = _dir.Combine("short.exe");
        File.WriteAllBytes(path, [(byte)'M', (byte)'Z']);

        Assert.Null(PeBitness.Read(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_ReturnNull_When_PathIsBlank(string? path) => Assert.Null(PeBitness.Read(path));

    [Fact]
    public void Should_ReturnNull_When_FileDoesNotExist() =>
        Assert.Null(PeBitness.Read(_dir.Combine("no-such.exe")));
}
