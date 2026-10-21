# NordicP3

Windows tool for Volvo P3 engine ECMs (3.2 B6324S, T6 B6304T2/T4) over the Volvo DiCE
cable. It talks to the cable through the DiCE driver's standard J2534 interface
(`TSDiCE32.dll`, 32-bit), so the only thing a PC needs installed is the DiCE driver.

## Build (once, on any Windows PC)

1. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
2. Double-click `build.bat`.
3. Copy `dist\NordicP3.exe` to the shop laptops. It is a single self-contained 32-bit file.

## What works now

| Feature | State |
|---|---|
| Find installed J2534 drivers (auto-selects DiCE), or pick a DLL by hand | Done |
| Check a DLL's bitness (32/64-bit) before loading it | Done |
| Open interface, show firmware / DLL / API versions | Done |
| Live supply voltage (OBD pin 16), colour-coded, 1 s refresh | Done |
| ISO 15765 channel to the ECM (500 kbit/s, 11-bit IDs by default) | Done |
| ECU info: VIN, calibration ID, CVN, ECU name (OBD mode 09) plus UDS identification DIDs | Done |
| Read stored/pending DTCs, clear DTCs | Done |
| Dump ECU to .bin (+ .json sidecar with info and SHA-256) | GUI + file handling done; read sequence pending captures |
| Write .bin to ECU | GUI + guardrails done; write sequence pending captures |
| Session log with every raw TX/RX byte | Done (`Documents\NordicP3\logs`) |

Write guardrails, all checked before anything is sent: file size must match the profile,
supply voltage must be at or above the profile minimum (12.5 V default), the user must
type FLASH to confirm, and the window cannot be closed mid-operation.

## Where things live

- `J2534/` - DLL loading, registry discovery, PE bitness check, device and ISO-TP channel.
- `Diagnostics/DiagClient.cs` - request/response, negative responses, response-pending (0x78), OBD and UDS helpers.
- `Diagnostics/ModuleProfile.cs` + `profiles.json` - all module-specific values (CAN IDs, DIDs, image size, min voltage).
  Put an edited `profiles.json` next to `NordicP3.exe` to override the built-in copy without rebuilding.
- `Diagnostics/FlashProcedure.cs` - `IFlashProcedure` (read image / write image). This is the one
  piece left to implement from the CAN captures.
- `MainWindow.xaml(.cs)` - the GUI.

## Next step: filling in read/write

Needed from a CAN capture of a full ECM read and write (VIDA or a commercial tool, with a
CANable/SavvyCAN logging the 500 k bus alongside the DiCE):

1. The diagnostic request/response CAN IDs actually used for programming.
2. Session control and any tester-present cadence.
3. The security-access exchange (seed/key) and how the ECM PIN fits in.
4. Read: memory addresses/lengths per request (or the upload sequence).
5. Write: erase routine, request download format, block size, transfer, checksum/verify routine, reset.

Implement those as a class implementing `IFlashProcedure`, register it in
`FlashProcedures.For()`, set `"configured": true` and `imageSizeBytes` in the profile, and
test only on a bench ECM with a known-good dump archived until it has passed repeated
read/write/verify cycles.

## First test on a car

1. Plug in DiCE, ignition on (engine off), charger connected.
2. Launch NordicP3.exe: the DiCE driver should be pre-selected and listed as 32-bit.
3. Connect -> voltage should read about 12-14 V.
4. Read ECU info, Read DTCs.
5. Send back the log file from `Documents\NordicP3\logs` - it shows exactly how the ECM answered.
