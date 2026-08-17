namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Owns the exact executable stream that has already passed the caller's cryptographic verification.
/// On Windows it captures stable file-object identity from that same open handle so later launch
/// verification can compare the authenticated object with the image selected for a suspended process.
/// This type does not perform hashing itself and must only be created after SHA-256 verification succeeds.
/// </summary>
internal sealed class VerifiedExecutableLease : IDisposable
{
    private VerifiedExecutableLease(FileStream stream, WindowsFileIdentity? windowsIdentity)
    {
        Stream = stream;
        WindowsIdentity = windowsIdentity;
    }

    public FileStream Stream { get; }

    public WindowsFileIdentity? WindowsIdentity { get; }

    public static VerifiedExecutableLease FromVerifiedStream(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.SafeFileHandle.IsClosed || stream.SafeFileHandle.IsInvalid)
        {
            throw new ArgumentException("Verified executable stream must expose a valid open file handle.", nameof(stream));
        }

        var identity = OperatingSystem.IsWindows()
            ? WindowsFileIdentity.FromHandle(stream.SafeFileHandle)
            : null;

        return new VerifiedExecutableLease(stream, identity);
    }

    public void Dispose() => Stream.Dispose();
}
