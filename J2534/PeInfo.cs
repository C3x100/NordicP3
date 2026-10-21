namespace NordicP3.J2534;

public enum PeArch { Unknown, X86, X64, Arm64, NotPe }

/// <summary>Reads a DLL's PE header to tell whether it is 32- or 64-bit, without loading it.</summary>
public static class PeInfo
{
    public static PeArch GetArch(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return PeArch.NotPe;   // "MZ"
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 6) return PeArch.NotPe;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return PeArch.NotPe;                    // "PE\0\0"
            return br.ReadUInt16() switch
            {
                0x014C => PeArch.X86,
                0x8664 => PeArch.X64,
                0xAA64 => PeArch.Arm64,
                _ => PeArch.Unknown,
            };
        }
        catch
        {
            return PeArch.Unknown;
        }
    }

    public static string Describe(PeArch a) => a switch
    {
        PeArch.X86 => "32-bit (x86)",
        PeArch.X64 => "64-bit (x64)",
        PeArch.Arm64 => "64-bit (ARM64)",
        PeArch.NotPe => "not a Windows DLL",
        _ => "unknown",
    };

    public static bool CanLoadInThisProcess(PeArch a) =>
        a == (Environment.Is64BitProcess ? PeArch.X64 : PeArch.X86);
}
