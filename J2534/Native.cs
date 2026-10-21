using System.Runtime.InteropServices;

namespace NordicP3.J2534;

/// <summary>SAE J2534-1 v04.04 constants and structures. "unsigned long" is 32-bit on Windows.</summary>
internal static class J2534Const
{
    // Protocol IDs
    public const uint CAN = 5;
    public const uint ISO15765 = 6;

    // Connect flags
    public const uint CAN_29BIT_ID = 0x100;

    // TxFlags
    public const uint ISO15765_FRAME_PAD = 0x40;

    // RxStatus bits
    public const uint TX_MSG_TYPE = 0x01;       // loopback of something we sent
    public const uint START_OF_MESSAGE = 0x02;  // first-frame indication, no payload yet
    public const uint TX_INDICATION = 0x08;

    // Filter types
    public const uint PASS_FILTER = 1;
    public const uint FLOW_CONTROL_FILTER = 3;

    // IOCTL IDs
    public const uint GET_CONFIG = 0x01;
    public const uint SET_CONFIG = 0x02;
    public const uint READ_VBATT = 0x03;
    public const uint CLEAR_TX_BUFFER = 0x07;
    public const uint CLEAR_RX_BUFFER = 0x08;

    // Status codes
    public const int STATUS_NOERROR = 0x00;
    public const int ERR_TIMEOUT = 0x09;
    public const int ERR_BUFFER_EMPTY = 0x10;

    public const int MaxData = 4128;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct PassThruMsg
{
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = J2534Const.MaxData)]
    public byte[] Data;

    public static PassThruMsg Create(uint protocol, uint txFlags = 0) => new()
    {
        ProtocolID = protocol,
        TxFlags = txFlags,
        Data = new byte[J2534Const.MaxData],
    };
}

// Function signatures. J2534 DLLs export WINAPI (__stdcall) functions.
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruOpenFn(IntPtr pName, out uint deviceId);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruCloseFn(uint deviceId);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruConnectFn(uint deviceId, uint protocolId, uint flags, uint baud, out uint channelId);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruDisconnectFn(uint channelId);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruReadMsgsFn(uint channelId, IntPtr pMsgs, ref uint numMsgs, uint timeout);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruWriteMsgsFn(uint channelId, IntPtr pMsgs, ref uint numMsgs, uint timeout);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruStartMsgFilterFn(uint channelId, uint filterType, IntPtr pMask, IntPtr pPattern, IntPtr pFlowControl, out uint filterId);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruIoctlFn(uint channelId, uint ioctlId, IntPtr pInput, IntPtr pOutput);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruReadVersionFn(uint deviceId, IntPtr pFirmware, IntPtr pDll, IntPtr pApi);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PassThruGetLastErrorFn(IntPtr pErrorDescription);

internal static class Kernel32
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr module);
}
