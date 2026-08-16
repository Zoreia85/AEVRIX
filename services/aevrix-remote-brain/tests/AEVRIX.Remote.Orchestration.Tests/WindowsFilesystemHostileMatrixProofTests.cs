using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsFilesystemHostileMatrixProofTests
{
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public void CompleteMatrix_IsAccepted()
    {
        var proof = Complete();

        var accepted = WindowsFilesystemHostileMatrixGate.RequireComplete(proof, Fingerprint);

        Assert.IsTrue(accepted.IsComplete);
        Assert.IsTrue(accepted.ReadIsolationProven);
        Assert.IsTrue(accepted.WriteBoundaryProven);
        Assert.IsTrue(accepted.WorkspaceCompatibilityProven);
    }

    [TestMethod]
    public void ExternalWriteDenial_DoesNotProveReadIsolation()
    {
        var proof = Complete() with { ControlledExternalReadDenied = false };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemHostileMatrixGate.RequireComplete(proof, Fingerprint));

        StringAssert.Contains(error.Message, "read isolation");
        Assert.IsTrue(proof.WriteBoundaryProven);
        Assert.IsFalse(proof.ReadIsolationProven);
    }

    [TestMethod]
    public void EachReadSentinel_IsMandatory()
    {
        var incomplete = new[]
        {
            Complete() with { ControlledExternalReadDenied = false },
            Complete() with { UserProfileReadDenied = false },
            Complete() with { RepositorySourceTreeReadDenied = false },
            Complete() with { TempReadDenied = false },
            Complete() with { SiblingWorkspaceReadDenied = false },
            Complete() with { ReparseEscapeDenied = false }
        };

        foreach (var proof in incomplete)
        {
            Assert.IsFalse(proof.ReadIsolationProven);
            Assert.Throws<InvalidDataException>(() =>
                WindowsFilesystemHostileMatrixGate.RequireComplete(proof, Fingerprint));
        }
    }

    [TestMethod]
    public void SiblingWorkspaceWrite_IsMandatory()
    {
        var proof = Complete() with { SiblingWorkspaceWriteDenied = false };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemHostileMatrixGate.RequireComplete(proof, Fingerprint));

        StringAssert.Contains(error.Message, "write boundary");
    }

    [TestMethod]
    public void RuntimeCompatibility_IsMandatory()
    {
        var proof = Complete() with { InWorkspaceRuntimeLoadSucceeded = false };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemHostileMatrixGate.RequireComplete(proof, Fingerprint));

        StringAssert.Contains(error.Message, "runtime compatibility");
    }

    [TestMethod]
    public void AuthorityMismatch_IsRejected()
    {
        var other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemHostileMatrixGate.RequireComplete(Complete(), other));

        StringAssert.Contains(error.Message, "different authority policy");
    }

    [TestMethod]
    public void InvalidFingerprint_IsRejected()
    {
        var proof = Complete() with { AuthorityFingerprint = "not-a-sha256" };

        Assert.Throws<ArgumentException>(() => proof.Validate());
    }

    private static WindowsFilesystemHostileMatrixProof Complete() => new(
        ControlledExternalReadDenied: true,
        UserProfileReadDenied: true,
        RepositorySourceTreeReadDenied: true,
        TempReadDenied: true,
        SiblingWorkspaceReadDenied: true,
        ReparseEscapeDenied: true,
        ControlledExternalWriteDenied: true,
        SiblingWorkspaceWriteDenied: true,
        InWorkspaceReadSucceeded: true,
        InWorkspaceWriteSucceeded: true,
        InWorkspaceRuntimeLoadSucceeded: true,
        AuthorityFingerprint: Fingerprint);
}
