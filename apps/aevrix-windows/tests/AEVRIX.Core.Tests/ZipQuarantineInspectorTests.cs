using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ZipQuarantineInspectorTests
{
    [TestMethod]
    public void InventoryListsEntriesWithoutExtraction()
    {
        using var stream = BuildArchive(("folder/readme.txt", "hello"), ("data/sample.json", "{}"));
        var result = ZipQuarantineInspector.Inspect(stream);

        Assert.AreEqual(2, result.EntryCount);
        CollectionAssert.AreEquivalent(
            new[] { "folder/readme.txt", "data/sample.json" },
            result.Entries.Select(static entry => entry.Path).ToArray());
    }

    [TestMethod]
    public void TraversalEntryIsRejectedBeforeDownstreamExtraction()
    {
        using var stream = BuildArchive(("../escape.txt", "blocked"));
        Assert.ThrowsExactly<InvalidDataException>(() => ZipQuarantineInspector.Inspect(stream));
    }

    [TestMethod]
    public void PathIsCanonicalizedInsideQuarantineRoot()
    {
        using var stream = BuildArchive(("a/../b/data.txt", "ok"));
        var result = ZipQuarantineInspector.Inspect(stream);

        Assert.AreEqual("b/data.txt", result.Entries.Single().Path);
    }

    [TestMethod]
    public void InputAndNestingLimitsAreEnforced()
    {
        using var oversized = BuildArchive(("payload.txt", new string('x', 128)));
        var inputPolicy = ArtifactQuarantinePolicy.Default with { MaxInputBytes = 1 };
        Assert.ThrowsExactly<InvalidDataException>(() => ZipQuarantineInspector.Inspect(oversized, inputPolicy));

        using var nested = BuildArchive(("a/b/c/data.txt", "ok"));
        var depthPolicy = ArtifactQuarantinePolicy.Default with { MaxNestingDepth = 2 };
        Assert.ThrowsExactly<InvalidDataException>(() => ZipQuarantineInspector.Inspect(nested, depthPolicy));
    }

    private static MemoryStream BuildArchive(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
