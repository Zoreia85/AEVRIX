using System.IO.Compression;

namespace Aevrix.Core;

public sealed record ZipEntryInspection(
    string Path,
    long CompressedBytes,
    long ExpandedBytes,
    bool IsDirectory);

public sealed record ZipContainerInspection(
    IReadOnlyList<ZipEntryInspection> Entries,
    long TotalCompressedBytes,
    long TotalExpandedBytes)
{
    public int EntryCount => Entries.Count;
}

/// <summary>
/// Read-only ZIP-family inventory for quarantine. It never extracts entry bytes.
/// Paths, file count and declared expanded size are validated before downstream work.
/// ZIP-based platform packages still require structure refinement after this inventory.
/// </summary>
public static class ZipQuarantineInspector
{
    public static ZipContainerInspection Inspect(
        Stream input,
        ArtifactQuarantinePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
            throw new ArgumentException("ZIP quarantine input must be readable.", nameof(input));

        var effectivePolicy = policy ?? ArtifactQuarantinePolicy.Default;
        if (!effectivePolicy.ReadOnly || effectivePolicy.NetworkAllowed || effectivePolicy.ExecutionAllowed)
            throw new ArgumentException("ZIP quarantine requires a read-only, offline, non-executing policy.", nameof(policy));

        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > effectivePolicy.MaxExtractedFiles)
            throw new InvalidDataException("ZIP entry count exceeds the quarantine file limit.");

        var entries = new List<ZipEntryInspection>(archive.Entries.Count);
        long totalCompressed = 0;
        long totalExpanded = 0;

        foreach (var entry in archive.Entries)
        {
            var safePath = NormalizeAndValidateRelativePath(entry.FullName);
            var isDirectory = entry.FullName.EndsWith('/', StringComparison.Ordinal) ||
                              entry.FullName.EndsWith('\\', StringComparison.Ordinal);

            totalCompressed = CheckedAdd(totalCompressed, entry.CompressedLength);
            totalExpanded = CheckedAdd(totalExpanded, entry.Length);

            if (totalExpanded > effectivePolicy.MaxExpandedBytes)
                throw new InvalidDataException("ZIP declared expanded size exceeds the quarantine expansion limit.");

            entries.Add(new ZipEntryInspection(
                safePath,
                entry.CompressedLength,
                entry.Length,
                isDirectory));
        }

        return new ZipContainerInspection(entries, totalCompressed, totalExpanded);
    }

    private static string NormalizeAndValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("ZIP entry path is empty.");

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/', StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("ZIP entry path is absolute or drive-qualified.");

        var depth = 0;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                depth--;
                if (depth < 0)
                    throw new InvalidDataException("ZIP entry path escapes the quarantine root.");
                continue;
            }

            depth++;
        }

        return normalized;
    }

    private static long CheckedAdd(long current, long value)
    {
        if (value < 0 || current > long.MaxValue - value)
            throw new InvalidDataException("ZIP size metadata is invalid or overflows quarantine accounting.");
        return current + value;
    }
}
