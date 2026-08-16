using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsFilesystemHostileMatrixProofTests
{
    [TestMethod]
    public void CompleteProof_MintsReadAndWriteAttestation()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint());

        var attestation = WindowsFilesystemIsolationAttestationFactory.Create(
            "windows-hostile-proof",
            authority,
            proof);

        Assert.AreEqual(authority.ComputeFingerprint(), attestation.AuthorityFingerprint);
        Assert.IsTrue(attestation.FilesystemWriteBoundaryEnforced);
        Assert.IsTrue(attestation.FilesystemReadIsolationEnforced);
    }

    [TestMethod]
    public void Factory_RejectsMissingExternalRead()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint()) with
        {
            UserProfileReadDenied = false
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create(
                "windows-hostile-proof",
                authority,
                proof));

        StringAssert.Contains(error.Message, "read isolation");
    }

    [TestMethod]
    public void Factory_RejectsMissingWriteBoundary()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint()) with
        {
            ControlledExternalWriteDenied = false
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create(
                "windows-hostile-proof",
                authority,
                proof));

        StringAssert.Contains(error.Message, "write boundary");
    }

    [TestMethod]
    public void Factory_RejectsBrokenWorkspaceRuntimeCompatibility()
    {
        var authority = RestrictedAuthority();
        var proof = Complete(authority.ComputeFingerprint()) with
        {
            InWorkspaceRuntimeLoadSucceeded = false
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create(
                "windows-hostile-proof",
                authority,
                proof));

        StringAssert.Contains(error.Message, "runtime compatibility");
    }

    [TestMethod]
    public void Factory_RejectsProofBoundToDifferentAuthority()
    {
        var authority = RestrictedAuthority();
        var stale = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceReadOnly));
        var proof = Complete(stale.ComputeFingerprint());

        var error = Assert.Throws<InvalidDataException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create(
                "windows-hostile-proof",
                authority,
                proof));

        StringAssert.Contains(error.Message, "different authority policy");
    }

    [TestMethod]
    public void Factory_RejectsUnrestrictedFilesystemAuthority()
    {
        var authority = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.Unrestricted),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));
        var proof = Complete(authority.ComputeFingerprint());

        Assert.Throws<InvalidOperationException>(() =>
            WindowsFilesystemIsolationAttestationFactory.Create(
                "windows-hostile-proof",
                authority,
                proof));
    }

    [TestMethod]
    public void Gate_RejectsInvalidExpectedFingerprint()
    {
        var proof = Complete(new string('a', 64));

        Assert.Throws<ArgumentException>(() =>
            WindowsFilesystemHostileMatrixGate.RequireComplete(proof, "not-a-sha"));
    }

    private static OutOfProcessAuthorityPolicy RestrictedAuthority() =>
        new(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

    private static WindowsFilesystemHostileMatrixProof Complete(string fingerprint) =>
        new(
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
