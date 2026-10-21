using System.Text;
using NordicP3.J2534;

namespace NordicP3.Diagnostics;

public sealed class NegativeResponseException(byte service, byte nrc)
    : Exception($"Module rejected service 0x{service:X2}: {Describe(nrc)} (NRC 0x{nrc:X2})")
{
    public byte Service { get; } = service;
    public byte Nrc { get; } = nrc;

    public static string Describe(byte nrc) => nrc switch
    {
        0x10 => "general reject",
        0x11 => "service not supported",
        0x12 => "sub-function not supported",
        0x13 => "incorrect message length",
        0x22 => "conditions not correct",
        0x24 => "request sequence error",
        0x31 => "request out of range",
        0x33 => "security access denied",
        0x35 => "invalid key",
        0x36 => "exceeded number of attempts",
        0x37 => "required time delay not expired",
        0x72 => "general programming failure",
        0x7E => "sub-function not supported in active session",
        0x7F => "service not supported in active session",
        _ => "unknown reason",
    };
}

/// <summary>Request/response on top of an open ISO 15765 channel (OBD-II and UDS style).</summary>
public sealed class DiagClient(J2534Device dev, int timeoutMs = 1000)
{
    /// <summary>Sends a request and returns the positive response (including the echoed service byte).</summary>
    public byte[] Request(params byte[] req)
    {
        dev.Send(req);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(deadline - Environment.TickCount64);
            var resp = left > 0 ? dev.Receive(left) : null;
            if (resp == null)
                throw new TimeoutException($"No response to 0x{req[0]:X2} within {timeoutMs} ms.");

            if (resp.Length >= 3 && resp[0] == 0x7F && resp[1] == req[0])
            {
                if (resp[2] == 0x78)                       // response pending: module is busy, keep waiting
                {
                    deadline = Environment.TickCount64 + 5000;
                    continue;
                }
                throw new NegativeResponseException(req[0], resp[2]);
            }
            if (resp[0] == (byte)(req[0] + 0x40)) return resp;
            // Anything else is unrelated traffic; ignore and keep waiting.
        }
    }

    public byte[]? TryRequest(params byte[] req)
    {
        try { return Request(req); }
        catch (NegativeResponseException) { return null; }
        catch (TimeoutException) { return null; }
    }

    // ---------- OBD-II (SAE J1979), answered by every OBD-compliant ECM ----------

    /// <summary>Mode 09 info type, e.g. 0x02 VIN, 0x04 calibration ID, 0x06 CVN, 0x0A ECU name.</summary>
    public byte[]? ObdVehicleInfo(byte infoType)
    {
        var r = TryRequest(0x09, infoType);
        if (r == null || r.Length < 3) return null;
        return r[3..];   // 49, infoType, itemCount, data...
    }

    public static string Ascii(byte[] data) =>
        Encoding.ASCII.GetString(data.Where(b => b >= 0x20 && b < 0x7F).ToArray()).Trim();

    /// <summary>Reads stored (mode 03) or pending (mode 07) DTCs.</summary>
    public List<string> ObdDtcs(bool pending)
    {
        var r = Request(pending ? (byte)0x07 : (byte)0x03);
        var list = new List<string>();
        // CAN: 43 <count> then pairs. Some ECMs omit the count byte; handle both.
        int start = (r.Length % 2 == 0) ? 2 : 1;
        for (int i = start; i + 1 < r.Length; i += 2)
        {
            if (r[i] == 0 && r[i + 1] == 0) continue;
            list.Add(FormatDtc(r[i], r[i + 1]));
        }
        return list;
    }

    public void ObdClearDtcs() => Request(0x04);

    public static string FormatDtc(byte a, byte b)
    {
        char sys = "PCBU"[a >> 6];
        return $"{sys}{(a >> 4) & 0x3}{a & 0xF:X}{b:X2}";
    }

    // ---------- UDS (ISO 14229) ----------

    public byte[]? UdsReadDid(ushort did)
    {
        var r = TryRequest(0x22, (byte)(did >> 8), (byte)did);
        return r is { Length: > 3 } ? r[3..] : null;
    }
}
