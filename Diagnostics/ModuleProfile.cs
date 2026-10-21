using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NordicP3.Diagnostics;

/// <summary>
/// Everything module-specific lives in profiles.json, not in code. When the CAN captures
/// arrive, the addresses, identifiers and read/write parameters get filled in there.
/// </summary>
public sealed class ModuleProfile
{
    public string Name { get; set; } = "";
    public string RequestId { get; set; } = "0x7E0";
    public string ResponseId { get; set; } = "0x7E8";
    public uint Baud { get; set; } = 500000;
    public bool ExtendedIds { get; set; }
    public bool ObdInfo { get; set; } = true;
    public List<DidEntry> Dids { get; set; } = new();
    public FlashConfig Flash { get; set; } = new();

    [JsonIgnore] public uint TxId => ParseHex(RequestId);
    [JsonIgnore] public uint RxId => ParseHex(ResponseId);

    public override string ToString() => Name;

    private static uint ParseHex(string s) =>
        uint.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber);
}

public sealed class DidEntry
{
    public string Did { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Ascii { get; set; } = true;

    [JsonIgnore] public ushort Value => ushort.Parse(Did, NumberStyles.HexNumber);
}

public sealed class FlashConfig
{
    /// <summary>False until the read/write sequence has been confirmed from captures and tested on a bench ECM.</summary>
    public bool Configured { get; set; }
    public long ImageSizeBytes { get; set; }
    public double MinVoltage { get; set; } = 12.5;
    public string Notes { get; set; } = "";
}

public sealed class ProfileSet
{
    public List<ModuleProfile> Modules { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Uses profiles.json next to the .exe if present, otherwise the built-in copy.</summary>
    public static (ProfileSet Set, string Source) Load()
    {
        var external = Path.Combine(AppContext.BaseDirectory, "profiles.json");
        if (File.Exists(external))
            return (JsonSerializer.Deserialize<ProfileSet>(File.ReadAllText(external), Opts)!, external);

        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NordicP3.profiles.json")!;
        return (JsonSerializer.Deserialize<ProfileSet>(s, Opts)!, "built-in defaults");
    }

    /// <summary>Writes the built-in profiles next to the .exe so they can be edited.</summary>
    public static string ExportDefaults()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profiles.json");
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NordicP3.profiles.json")!;
        using var f = File.Create(path);
        s.CopyTo(f);
        return path;
    }
}
