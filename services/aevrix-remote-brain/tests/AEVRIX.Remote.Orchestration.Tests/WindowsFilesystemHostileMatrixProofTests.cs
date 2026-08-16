using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsFilesystemHostileMatrixProofTests
{
    [TestMethod]
    public void CompleteMatrix_MintsGranularFilesystemAttestation()
    {
        var authority = Restricted();
        var result = WindowsFilesystemIsolationAttestationFactory.Create("hostile-proof-backend", authority, Complete(authority.ComputeFingerprint()));
        Assert.IsTrue(result.FilesystemReadIsolationEnforced);
        Assert.IsTrue(result.FilesystemWriteBoundaryEnforced);
        Assert.AreEqual(authority.ComputeFingerprint(), result.AuthorityFingerprint);
    }

    [TestMethod]
    public void ExternalWriteDenial_AloneCannotMintReadIsolation()
    {
        var authority = Restricted();
        var proof = Complete(authority.ComputeFingerprint()) with { ControlledExternalReadDenied = false };
        var error = Assert.Throws<InvalidDataException>(() => WindowsFilesystemIsolationAttestationFactory.Create("write-only", authority, proof));
        StringAssert.Contains(error.Message, "read isolation");
        Assert.IsTrue(proof.WriteBoundaryProven);
        Assert.IsFalse(proof.ReadIsolationProven);
    }

    [TestMethod]
    public void EveryHostileReadSentinel_IsMandatory()
    {
        var authority = Restricted(); var fingerprint = authority.ComputeFingerprint();
        var incomplete = new[]
        {
            Complete(fingerprint) with { ControlledExternalReadDenied = false },
            Complete(fingerprint) with { UserProfileReadDenied = false },
            Complete(fingerprint) with { RepositorySourceTreeReadDenied = false },
            Complete(fingerprint) with { TempReadDenied = false },
            Complete(fingerprint) with { SiblingWorkspaceReadDenied = false },
            Complete(fingerprint) with { ReparseEscapeDenied = false }
        };
        foreach (var proof in incomplete)
            Assert.Throws<InvalidDataException>(() => WindowsFilesystemIsolationAttestationFactory.Create("incomplete-read", authority, proof));
    }

    [TestMethod]
    public void RuntimeCompatibility_IsMandatory()
    {
        var authority = Restricted();
        var proof = Complete(authority.ComputeFingerprint()) with { InWorkspaceRuntimeLoadSucceeded = false };
        var error = Assert.Throws<InvalidDataException>(() => WindowsFilesystemIsolationAttestationFactory.Create("runtime-broken", authority, proof));
        StringAssert.Contains(error.Message, "runtime compatibility");
    }

    [TestMethod]
    public void AuthorityReplay_IsRejected()
    {
        var authority = Restricted();
        var other = new OutOfProcessAuthorityPolicy(new(OutOfProcessNetworkScope.None), new(OutOfProcessFilesystemScope.WorkspaceReadOnly));
        var error = Assert.Throws<InvalidDataException>(() => WindowsFilesystemIsolationAttestationFactory.Create("replay", authority, Complete(other.ComputeFingerprint())));
        StringAssert.Contains(error.Message, "different authority policy");
    }

    [TestMethod]
    public void UnrestrictedFilesystem_CannotReceiveHostileIsolationAttestation()
    {
        var authority = new OutOfProcessAuthorityPolicy(new(OutOfProcessNetworkScope.None), new(OutOfProcessFilesystemScope.Unrestricted));
        Assert.Throws<InvalidOperationException>(() => WindowsFilesystemIsolationAttestationFactory.Create("wrong-scope", authority, Complete(authority.ComputeFingerprint())));
    }

    private static OutOfProcessAuthorityPolicy Restricted() => new(new(OutOfProcessNetworkScope.None), new(OutOfProcessFilesystemScope.WorkspaceOnly));

    private static WindowsFilesystemHostileMatrixProof Complete(string fingerprint) => new(
        true, true, true, true, true, true, true, true, true, true, true, fingerprint);
}
