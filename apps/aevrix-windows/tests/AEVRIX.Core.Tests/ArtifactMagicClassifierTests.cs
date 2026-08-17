using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class ArtifactMagicClassifierTests
{
    [TestMethod]
    public void DetectsRepresentativeArtifactFamiliesByContentSignature()
    {
        var cases = new[]
        {
            (new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, TargetKind.WindowsExecutable),
            (new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, TargetKind.NativeOrBytecodeArtifact),
            (new byte[] { 0x00, 0x61, 0x73, 0x6D }, TargetKind.WebAssemblyModule),
            (System.Text.Encoding.ASCII.GetBytes("%PDF-1.7"), TargetKind.DocumentArtifact),
            (System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0rest"), TargetKind.DatabaseArtifact),
            (new byte[] { 0x50, 0x4B, 0x03, 0x04 }, TargetKind.ArchiveContainer),
            (new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, TargetKind.ArchiveContainer),
            (new byte[] { 0x51, 0x46, 0x49, 0xFB }, TargetKind.VirtualMachineImage)
        };

        foreach (var (prefix, expectedKind) in cases)
        {
            var detection = ArtifactMagicClassifier.Detect(prefix);
            Assert.AreEqual(expectedKind, detection.Kind);
            Assert.IsTrue(detection.IsKnown);
        }
    }

    [TestMethod]
    public void ZipSignatureRemainsBroadUntilStructureRefinement()
    {
        var detection = ArtifactMagicClassifier.Detect(new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        Assert.AreEqual(TargetKind.ArchiveContainer, detection.Kind);
        Assert.AreEqual("ZIP-family", detection.Format);
        Assert.IsTrue(detection.IsContainer);
        Assert.IsTrue(detection.RequiresStructureRefinement);
    }

    [TestMethod]
    public void UnknownPrefixDoesNotInventAFormat()
    {
        var detection = ArtifactMagicClassifier.Detect(new byte[] { 0x01, 0x02, 0x03, 0x04 });

        Assert.AreEqual(TargetKind.Unknown, detection.Kind);
        Assert.IsFalse(detection.IsKnown);
        Assert.IsTrue(detection.RequiresStructureRefinement);
    }
}
