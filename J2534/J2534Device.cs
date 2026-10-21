using System.Runtime.InteropServices;

namespace NordicP3.J2534;

/// <summary>
/// One open J2534 interface plus (optionally) one ISO 15765 channel to a single module.
/// Not thread-safe: callers serialize access (MainWindow uses a SemaphoreSlim).
/// </summary>
public sealed class J2534Device : IDisposable
{
    private readonly PassThruLibrary _lib;
    private readonly uint _deviceId;
    private uint? _channelId;
    private uint _txId, _rxId;

    /// <summary>Raw traffic hook for the log: (direction, bytes).</summary>
    public Action<string, byte[]>? Trace { get; set; }

    public DriverInfo Driver { get; }
    public bool ChannelOpen => _channelId.HasValue;

    private J2534Device(DriverInfo driver, PassThruLibrary lib, uint deviceId)
    {
        Driver = driver; _lib = lib; _deviceId = deviceId;
    }

    public static J2534Device Open(DriverInfo driver)
    {
        var lib = new PassThruLibrary(driver.LibraryPath);
        int rc = lib.Open(IntPtr.Zero, out var id);
        if (rc != J2534Const.STATUS_NOERROR)
        {
            var err = lib.LastError();
            lib.Dispose();
            throw new J2534Exception($"PassThruOpen failed ({rc:X2}): {err}. Is the DiCE plugged in and powered from the car or bench supply?");
        }
        return new J2534Device(driver, lib, id);
    }

    public (string Firmware, string Dll, string Api) ReadVersion()
    {
        IntPtr f = Marshal.AllocHGlobal(80), d = Marshal.AllocHGlobal(80), a = Marshal.AllocHGlobal(80);
        try
        {
            Check(_lib.ReadVersion(_deviceId, f, d, a), "PassThruReadVersion");
            return (Marshal.PtrToStringAnsi(f) ?? "", Marshal.PtrToStringAnsi(d) ?? "", Marshal.PtrToStringAnsi(a) ?? "");
        }
        finally { Marshal.FreeHGlobal(f); Marshal.FreeHGlobal(d); Marshal.FreeHGlobal(a); }
    }

    /// <summary>Voltage on OBD pin 16 (vehicle battery / bench supply), in volts.</summary>
    public double ReadBatteryVolts()
    {
        var p = Marshal.AllocHGlobal(4);
        try
        {
            Check(_lib.Ioctl(_deviceId, J2534Const.READ_VBATT, IntPtr.Zero, p), "READ_VBATT");
            return (uint)Marshal.ReadInt32(p) / 1000.0;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>Opens an ISO 15765 (ISO-TP over CAN) channel to one module with a flow-control filter.</summary>
    public void OpenChannel(uint txId, uint rxId, uint baud, bool extendedIds)
    {
        CloseChannel();
        uint flags = extendedIds ? J2534Const.CAN_29BIT_ID : 0;
        Check(_lib.Connect(_deviceId, J2534Const.ISO15765, flags, baud, out var ch), "PassThruConnect");
        _channelId = ch; _txId = txId; _rxId = rxId;

        var mask = MakeIdMsg(0xFFFFFFFF, extendedIds);
        var pattern = MakeIdMsg(rxId, extendedIds);
        var flow = MakeIdMsg(txId, extendedIds);
        using var pm = new PinnedMsg(mask);
        using var pp = new PinnedMsg(pattern);
        using var pf = new PinnedMsg(flow);
        Check(_lib.StartMsgFilter(ch, J2534Const.FLOW_CONTROL_FILTER, pm.Ptr, pp.Ptr, pf.Ptr, out _), "PassThruStartMsgFilter");
        _lib.Ioctl(ch, J2534Const.CLEAR_RX_BUFFER, IntPtr.Zero, IntPtr.Zero);
    }

    public void CloseChannel()
    {
        if (_channelId is uint ch) { _lib.Disconnect(ch); _channelId = null; }
    }

    /// <summary>Sends one diagnostic request (service + data, no CAN ID).</summary>
    public void Send(byte[] payload, uint timeoutMs = 1000)
    {
        var ch = _channelId ?? throw new J2534Exception("No channel open.");
        var msg = PassThruMsg.Create(J2534Const.ISO15765, J2534Const.ISO15765_FRAME_PAD);
        WriteId(msg.Data, _txId);
        Buffer.BlockCopy(payload, 0, msg.Data, 4, payload.Length);
        msg.DataSize = (uint)(4 + payload.Length);
        Trace?.Invoke("TX", payload);

        using var pin = new PinnedMsg(msg);
        uint n = 1;
        _lib.Ioctl(ch, J2534Const.CLEAR_RX_BUFFER, IntPtr.Zero, IntPtr.Zero);
        Check(_lib.WriteMsgs(ch, pin.Ptr, ref n, timeoutMs), "PassThruWriteMsgs");
    }

    /// <summary>Waits for the next real response payload from the module, or null on timeout.</summary>
    public byte[]? Receive(int timeoutMs)
    {
        var ch = _channelId ?? throw new J2534Exception("No channel open.");
        var deadline = Environment.TickCount64 + timeoutMs;
        var msg = PassThruMsg.Create(J2534Const.ISO15765);
        using var pin = new PinnedMsg(msg);

        while (Environment.TickCount64 < deadline)
        {
            uint n = 1;
            var remaining = (uint)Math.Max(1, deadline - Environment.TickCount64);
            int rc = _lib.ReadMsgs(ch, pin.Ptr, ref n, Math.Min(remaining, 100u));
            if (rc == J2534Const.ERR_BUFFER_EMPTY || rc == J2534Const.ERR_TIMEOUT || n == 0) continue;
            Check(rc, "PassThruReadMsgs");

            var got = pin.Read();
            const uint skip = J2534Const.TX_MSG_TYPE | J2534Const.START_OF_MESSAGE | J2534Const.TX_INDICATION;
            if ((got.RxStatus & skip) != 0 || got.DataSize <= 4) continue;

            var payload = new byte[got.DataSize - 4];
            Buffer.BlockCopy(got.Data, 4, payload, 0, payload.Length);
            Trace?.Invoke("RX", payload);
            return payload;
        }
        return null;
    }

    private static PassThruMsg MakeIdMsg(uint id, bool ext)
    {
        var m = PassThruMsg.Create(J2534Const.ISO15765);
        WriteId(m.Data, id);
        m.DataSize = 4;
        if (ext) m.TxFlags |= J2534Const.CAN_29BIT_ID;
        return m;
    }

    private static void WriteId(byte[] data, uint id)
    {
        data[0] = (byte)(id >> 24); data[1] = (byte)(id >> 16);
        data[2] = (byte)(id >> 8); data[3] = (byte)id;
    }

    private void Check(int rc, string what)
    {
        if (rc != J2534Const.STATUS_NOERROR)
            throw new J2534Exception($"{what} failed ({rc:X2}): {_lib.LastError()}");
    }

    public void Dispose()
    {
        try { CloseChannel(); } catch { }
        try { _lib.Close(_deviceId); } catch { }
        _lib.Dispose();
    }

    /// <summary>Keeps a PASSTHRU_MSG in unmanaged memory for the duration of a call.</summary>
    private sealed class PinnedMsg : IDisposable
    {
        public IntPtr Ptr { get; }
        public PinnedMsg(PassThruMsg m)
        {
            Ptr = Marshal.AllocHGlobal(Marshal.SizeOf<PassThruMsg>());
            Marshal.StructureToPtr(m, Ptr, false);
        }
        public PassThruMsg Read() => Marshal.PtrToStructure<PassThruMsg>(Ptr);
        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }
}
