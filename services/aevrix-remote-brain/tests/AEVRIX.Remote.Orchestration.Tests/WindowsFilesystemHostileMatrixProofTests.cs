using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsFilesystemHostileMatrixProofTests
{
    [TestMethod]
    public void CompleteMatrix_MintsGranularFilesystemAttestation()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint());

        var attestation = WindowsFilesystemIsolationAttestationFactory.Create(
            "hostile-proof-backend",
            authority,
            proof);

        Assert.IsTrue(attestation.FilesystemReadIsolationEnforced);
        Assert.IsTrue(attestation.FilesystemWriteBoundaryEnforced);
        Assert.AreEqual(authority.ComputeFingerprint(), attestation.AuthorityFingerprint);
    }

    [TestMethod]
    public void ExternalWriteDenial_AloneCannotMintReadIsolation()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint()) with
        {
            ControlledExternalReadDenied = false
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create("write-only", authority, proof));

        StringAssert.Contains(error.Message, "read isolation");
        Assert.IsTrue(proof.WriteBoundaryProven);
        Assert.IsFalse(proof.ReadIsolationProven);
    }

    [TestMethod]
    public void EveryHostileReadSentinel_IsMandatory()
    {
        var authority = RestrictedAuthority();
        var fingerprint = authority.ComputeFingerprint();
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
        {
            Assert.IsFalse(proof.ReadIsolationProven);
            Assert.Throws<InvalidDataException>(() =>
                WindowsFilesystemIsolationAttestationFactory.Create("incomplete-read", authority, proof));
        }
    }

    [TestMethod]
    public void WorkspaceCompatibility_IsMandatory()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint()) with
        {
            InWorkspaceRuntimeLoadSucceeded = false
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create("runtime-broken", authority, proof));

        StringAssert.Contains(error.Message, "runtime compatibility");
    }

    [TestMethod]
    public void AuthorityReplay_IsRejected()
    {
        var authority = RestrictedAuthority();
        var other = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceReadOnly));
        var proof = Complete(other.ComputeFingerprint());

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create("replay", authority, proof));

        StringAssert.Contains(error.Message, "different authority policy");
    }

    [TestMethod]
    public void UnrestrictedFilesystem_CannotReceiveHostileIsolationAttestation()
    {
        var authority = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));
        var proof = Complete(authority.ComputeFingerprint());

        Assert.Throws<InvalidOperationException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create("wrong-scope", authority, proof));
    }

    private static OutOfProcessAuthorityPolicy RestrictedAuthority() =>
        new(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

    private static WindowsFilesystemHostileMatrixProof Complete(string fingerprint) => new(
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
        AuthorityFingerprint: fingerprint);
}
