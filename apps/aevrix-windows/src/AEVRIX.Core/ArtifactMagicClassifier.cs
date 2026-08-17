namespace Aevrix.Core;

public sealed record ArtifactMagicDetection(
    TargetKind Kind,
    string Format,
    bool IsContainer,
    bool RequiresStructureRefinement)
{
    public bool IsKnown => Kind is not TargetKind.Unknown;
}

/// <summary>
/// Fast first-pass classifier for captured bytes. It deliberately returns broad families
/// when a signature is shared by multiple formats (for example ZIP-based APK/JAR/Office
/// packages). Structure refinement must run before platform-specific conclusions.
/// </summary>
public static class ArtifactMagicClassifier
{
    public static ArtifactMagicDetection Detect(ReadOnlySpan<byte> prefix)
    {
        if (StartsWith(prefix, 0x4D, 0x5A))
            return new(TargetKind.WindowsExecutable, "PE-family", false, true);

        if (StartsWith(prefix, 0x7F, 0x45, 0x4C, 0x46))
            return new(TargetKind.NativeOrBytecodeArtifact, "ELF", false, true);

        if (StartsWith(prefix, 0x00, 0x61, 0x73, 0x6D))
            return new(TargetKind.WebAssemblyModule, "WebAssembly", false, false);

        if (StartsWithAscii(prefix, "%PDF-"))
            return new(TargetKind.DocumentArtifact, "PDF", false, false);

        if (StartsWithAscii(prefix, "SQLite format 3\0"))
            return new(TargetKind.DatabaseArtifact, "SQLite3", false, false);

        if (StartsWith(prefix, 0x50, 0x4B, 0x03, 0x04) ||
            StartsWith(prefix, 0x50, 0x4B, 0x05, 0x06) ||
            StartsWith(prefix, 0x50, 0x4B, 0x07, 0x08))
            return new(TargetKind.ArchiveContainer, "ZIP-family", true, true);

        if (StartsWith(prefix, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
            return new(TargetKind.ArchiveContainer, "7z", true, false);

        if (StartsWith(prefix, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07))
            return new(TargetKind.ArchiveContainer, "RAR", true, true);

        if (StartsWith(prefix, 0x1F, 0x8B))
            return new(TargetKind.ArchiveContainer, "gzip", true, false);

        if (StartsWith(prefix, 0x42, 0x5A, 0x68))
            return new(TargetKind.ArchiveContainer, "bzip2", true, false);

        if (StartsWith(prefix, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00))
            return new(TargetKind.ArchiveContainer, "xz", true, false);

        if (StartsWith(prefix, 0x28, 0xB5, 0x2F, 0xFD))
            return new(TargetKind.ArchiveContainer, "zstd", true, false);

        if (StartsWith(prefix, 0x51, 0x46, 0x49, 0xFB))
            return new(TargetKind.VirtualMachineImage, "QCOW", true, true);

        if (StartsWithAscii(prefix, "dex\n"))
            return new(TargetKind.NativeOrBytecodeArtifact, "Android DEX", false, true);

        if (StartsWith(prefix, 0xFE, 0xED, 0xFA, 0xCE) ||
            StartsWith(prefix, 0xFE, 0xED, 0xFA, 0xCF) ||
            StartsWith(prefix, 0xCE, 0xFA, 0xED, 0xFE) ||
            StartsWith(prefix, 0xCF, 0xFA, 0xED, 0xFE) ||
            StartsWith(prefix, 0xCA, 0xFE, 0xBA, 0xBE))
            return new(TargetKind.NativeOrBytecodeArtifact, "Mach-O/fat-family", false, true);

        return new(TargetKind.Unknown, "unknown", false, true);
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, params byte[] expected) =>
        value.Length >= expected.Length && value[..expected.Length].SequenceEqual(expected);

    private static bool StartsWithAscii(ReadOnlySpan<byte> value, string expected)
    {
        if (value.Length < expected.Length)
            return false;

        for (var index = 0; index < expected.Length; index++)
        {
            if (value[index] != (byte)expected[index])
                return false;
        }

        return true;
    }
}
