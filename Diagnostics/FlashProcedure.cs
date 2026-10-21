using NordicP3.J2534;

namespace NordicP3.Diagnostics;

/// <summary>
/// Reads the module image to a byte array and writes one back. This is the part that
/// depends on the captured session sequence (session control, security access, memory
/// read / request download, transfer, verify) and is intentionally not implemented yet.
/// </summary>
public interface IFlashProcedure
{
    Task<byte[]> ReadImageAsync(IProgress<double> progress, CancellationToken ct);
    Task WriteImageAsync(byte[] image, IProgress<double> progress, CancellationToken ct);
}

public static class FlashProcedures
{
    public static IFlashProcedure For(ModuleProfile profile, J2534Device device, DiagClient diag) =>
        profile.Flash.Configured
            ? throw new NotImplementedException(
                  $"Profile '{profile.Name}' is marked configured, but no procedure is registered for it in FlashProcedures.For().")
            : new NotConfigured(profile);

    private sealed class NotConfigured(ModuleProfile p) : IFlashProcedure
    {
        private string Msg => $"Read/write is not set up for '{p.Name}' yet.\n\n" +
                              "It needs the session, security-access and transfer sequence from a CAN capture " +
                              "of a full read and write, verified on a bench ECM first." +
                              (string.IsNullOrWhiteSpace(p.Flash.Notes) ? "" : $"\n\nProfile notes: {p.Flash.Notes}");

        public Task<byte[]> ReadImageAsync(IProgress<double> progress, CancellationToken ct) =>
            throw new NotSupportedException(Msg);

        public Task WriteImageAsync(byte[] image, IProgress<double> progress, CancellationToken ct) =>
            throw new NotSupportedException(Msg);
    }
}
