using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace NordicP3.J2534;

/// <summary>A J2534 interface registered in Windows (e.g. Volvo DiCE -> TSDiCE32.dll).</summary>
public sealed record DriverInfo(string Name, string Vendor, string LibraryPath, bool SupportsIso15765, PeArch Arch)
{
    public bool Loadable => File.Exists(LibraryPath) && PeInfo.CanLoadInThisProcess(Arch);

    public override string ToString()
    {
        var note = !File.Exists(LibraryPath) ? "  [DLL missing]"
                 : !Loadable ? $"  [{PeInfo.Describe(Arch)} - not loadable]"
                 : "";
        return $"{Name} ({Vendor}){note}";
    }

    /// <summary>
    /// Finds installed J2534 drivers. Installers write to
    /// HKLM\SOFTWARE\PassThruSupport.04.04 (32-bit view lands under WOW6432Node).
    /// Both views are checked so a 64-bit-only install is still listed and explained.
    /// </summary>
    public static List<DriverInfo> Discover()
    {
        var found = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var root = hklm.OpenSubKey(@"SOFTWARE\PassThruSupport.04.04");
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    var lib = k?.GetValue("FunctionLibrary") as string;
                    if (string.IsNullOrWhiteSpace(lib) || found.ContainsKey(lib)) continue;
                    var iso = Convert.ToInt32(k!.GetValue("ISO15765") ?? 0) != 0;
                    found[lib] = new DriverInfo(
                        k.GetValue("Name") as string ?? sub,
                        k.GetValue("Vendor") as string ?? "",
                        lib, iso, PeInfo.GetArch(lib));
                }
            }
            catch { /* view unavailable */ }
        }
        return found.Values.OrderBy(d => !d.Loadable).ThenBy(d => d.Name).ToList();
    }

    public static DriverInfo FromFile(string path) =>
        new(Path.GetFileNameWithoutExtension(path), "manual", path, true, PeInfo.GetArch(path));
}

/// <summary>Loads a J2534 DLL and binds its exported functions.</summary>
internal sealed class PassThruLibrary : IDisposable
{
    private IntPtr _module;

    public PassThruOpenFn Open { get; }
    public PassThruCloseFn Close { get; }
    public PassThruConnectFn Connect { get; }
    public PassThruDisconnectFn Disconnect { get; }
    public PassThruReadMsgsFn ReadMsgs { get; }
    public PassThruWriteMsgsFn WriteMsgs { get; }
    public PassThruStartMsgFilterFn StartMsgFilter { get; }
    public PassThruIoctlFn Ioctl { get; }
    public PassThruReadVersionFn ReadVersion { get; }
    public PassThruGetLastErrorFn GetLastError { get; }

    public PassThruLibrary(string path)
    {
        var arch = PeInfo.GetArch(path);
        if (!PeInfo.CanLoadInThisProcess(arch))
            throw new J2534Exception($"{Path.GetFileName(path)} is {PeInfo.Describe(arch)}; this program is " +
                                     (Environment.Is64BitProcess ? "64-bit" : "32-bit") + " and cannot load it.");

        _module = Kernel32.LoadLibrary(path);
        if (_module == IntPtr.Zero)
            throw new J2534Exception($"LoadLibrary failed for {path} (Win32 error {Marshal.GetLastWin32Error()}).");

        Open = Bind<PassThruOpenFn>("PassThruOpen");
        Close = Bind<PassThruCloseFn>("PassThruClose");
        Connect = Bind<PassThruConnectFn>("PassThruConnect");
        Disconnect = Bind<PassThruDisconnectFn>("PassThruDisconnect");
        ReadMsgs = Bind<PassThruReadMsgsFn>("PassThruReadMsgs");
        WriteMsgs = Bind<PassThruWriteMsgsFn>("PassThruWriteMsgs");
        StartMsgFilter = Bind<PassThruStartMsgFilterFn>("PassThruStartMsgFilter");
        Ioctl = Bind<PassThruIoctlFn>("PassThruIoctl");
        ReadVersion = Bind<PassThruReadVersionFn>("PassThruReadVersion");
        GetLastError = Bind<PassThruGetLastErrorFn>("PassThruGetLastError");
    }

    private T Bind<T>(string name) where T : Delegate
    {
        var p = Kernel32.GetProcAddress(_module, name);
        if (p == IntPtr.Zero) throw new J2534Exception($"Driver does not export {name}.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public string LastError()
    {
        var buf = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.WriteByte(buf, 0);
            GetLastError(buf);
            return Marshal.PtrToStringAnsi(buf) ?? "";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_module != IntPtr.Zero) { Kernel32.FreeLibrary(_module); _module = IntPtr.Zero; }
    }
}

public sealed class J2534Exception(string message) : Exception(message);
