namespace NordicP3;

/// <summary>Session log written to Documents\NordicP3\logs, including raw TX/RX for later analysis.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NordicP3");
    public static string FilePath { get; } =
        Path.Combine(Folder, "logs", $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    /// <summary>(line, isRawTraffic)</summary>
    public static event Action<string, bool>? Line;

    public static void Write(string text, bool raw = false)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never break the tool */ }
        }
        Line?.Invoke(line, raw);
    }

    public static string Hex(byte[] b) => string.Join(' ', b.Select(x => x.ToString("X2")));
}
