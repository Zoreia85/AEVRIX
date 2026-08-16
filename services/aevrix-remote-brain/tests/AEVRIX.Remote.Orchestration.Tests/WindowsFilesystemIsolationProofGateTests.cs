using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class WindowsFilesystemIsolationProofGateTests
{
    private const string ExpectedExecutableSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public void CompleteHostileProof_ProvesWorkspaceOnlyReadAndWriteIsolation()
    {
        var authority = WorkspaceOnlyAuthority();
        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            CompleteEvidence(authority),
            ExpectedExecutableSha256,
            authority);

        Assert.IsTrue(decision.ExecutableIdentityBound);
        Assert.IsTrue(decision.AuthorityPolicyBound);
        Assert.IsTrue(decision.FilesystemWriteBoundaryEnforced);
        Assert.IsTrue(decision.FilesystemReadIsolationEnforced);
        Assert.IsTrue(decision.WorkspaceOnlyIsolationProven);
        Assert.AreEqual("WorkspaceOnlyIsolationProven", decision.DecisionCode);
    }

    [TestMethod]
    public void ExternalWriteDenial_DoesNotImplyReadIsolation()
    {
        var authority = WorkspaceOnlyAuthority();
        var evidence = CompleteEvidence(authority) with { OutsideReadDenied = false };

        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            evidence,
            ExpectedExecutableSha256,
            authority);

        Assert.IsTrue(decision.FilesystemWriteBoundaryEnforced);
        Assert.IsFalse(decision.FilesystemReadIsolationEnforced);
        Assert.IsFalse(decision.WorkspaceOnlyIsolationProven);
        Assert.AreEqual("OutsideReadNotDenied", decision.DecisionCode);
    }

    [TestMethod]
    public void ExternalReadDenial_DoesNotImplyWriteBoundary()
    {
        var authority = WorkspaceOnlyAuthority();
        var evidence = CompleteEvidence(authority) with { OutsideWriteDenied = false };

        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            evidence,
            ExpectedExecutableSha256,
            authority);

        Assert.IsFalse(decision.FilesystemWriteBoundaryEnforced);
        Assert.IsTrue(decision.FilesystemReadIsolationEnforced);
        Assert.IsFalse(decision.WorkspaceOnlyIsolationProven);
        Assert.AreEqual("OutsideWriteNotDenied", decision.DecisionCode);
    }

    [TestMethod]
    public void ExecutableHashMismatch_FailsClosedBeforeBoundaryClaims()
    {
        var authority = WorkspaceOnlyAuthority();
        var evidence = CompleteEvidence(authority) with
        {
            ExecutableSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            evidence,
            ExpectedExecutableSha256,
            authority);

        Assert.IsFalse(decision.ExecutableIdentityBound);
        Assert.IsFalse(decision.AuthorityPolicyBound);
        Assert.IsFalse(decision.FilesystemWriteBoundaryEnforced);
        Assert.IsFalse(decision.FilesystemReadIsolationEnforced);
        Assert.IsFalse(decision.WorkspaceOnlyIsolationProven);
        Assert.AreEqual("ExecutableSha256Mismatch", decision.DecisionCode);
    }

    [TestMethod]
    public void AuthorityFingerprintMismatch_FailsClosedBeforeBoundaryClaims()
    {
        var authority = WorkspaceOnlyAuthority();
        var evidence = CompleteEvidence(authority) with
        {
            AuthorityFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };

        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            evidence,
            ExpectedExecutableSha256,
            authority);

        Assert.IsTrue(decision.ExecutableIdentityBound);
        Assert.IsFalse(decision.AuthorityPolicyBound);
        Assert.IsFalse(decision.FilesystemWriteBoundaryEnforced);
        Assert.IsFalse(decision.FilesystemReadIsolationEnforced);
        Assert.IsFalse(decision.WorkspaceOnlyIsolationProven);
        Assert.AreEqual("AuthorityFingerprintMismatch", decision.DecisionCode);
    }

    [TestMethod]
    [DataRow("job", "RaceFreeJobAssignmentUnproven")]
    [DataRow("appcontainer", "AppContainerIdentityUnproven")]
    [DataRow("sid", "RestrictingSidUnproven")]
    [DataRow("acl", "WorkspaceAclUnproven")]
    [DataRow("inside-read", "WorkspaceReadUnproven")]
    [DataRow("inside-write", "WorkspaceWriteUnproven")]
    [DataRow("outside-read", "OutsideReadNotDenied")]
    [DataRow("outside-write", "OutsideWriteNotDenied")]
    public void MissingMandatoryProofFact_NeverPromotesWorkspaceOnly(string missing, string expectedCode)
    {
        var authority = WorkspaceOnlyAuthority();
        var evidence = Without(CompleteEvidence(authority), missing);

        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            evidence,
            ExpectedExecutableSha256,
            authority);

        Assert.IsFalse(decision.WorkspaceOnlyIsolationProven);
        Assert.AreEqual(expectedCode, decision.DecisionCode);
    }

    [TestMethod]
    public void NonWorkspaceOnlyAuthority_CannotUseWorkspaceOnlyProof()
    {
        var workspaceAuthority = WorkspaceOnlyAuthority();
        var unrestricted = new OutOfProcessAuthorityPolicy(
            new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
            new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.Unrestricted));

        var decision = new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
            CompleteEvidence(workspaceAuthority),
            ExpectedExecutableSha256,
            unrestricted);

        Assert.IsFalse(decision.WorkspaceOnlyIsolationProven);
        Assert.IsFalse(decision.FilesystemWriteBoundaryEnforced);
        Assert.IsFalse(decision.FilesystemReadIsolationEnforced);
        Assert.AreEqual("WorkspaceOnlyAuthorityRequired", decision.DecisionCode);
    }

    [TestMethod]
    public void InvalidDigestShape_IsRejectedInsteadOfNormalized()
    {
        var authority = WorkspaceOnlyAuthority();
        var evidence = CompleteEvidence(authority) with { ExecutableSha256 = "not-a-sha" };

        Assert.ThrowsExactly<ArgumentException>(() =>
            new WindowsFilesystemIsolationProofGate().EvaluateWorkspaceOnly(
                evidence,
                ExpectedExecutableSha256,
                authority));
    }

    private static OutOfProcessAuthorityPolicy WorkspaceOnlyAuthority() => new(
        new OutOfProcessNetworkPolicy(OutOfProcessNetworkScope.None),
        new OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope.WorkspaceOnly));

    private static WindowsFilesystemIsolationProofEvidence CompleteEvidence(OutOfProcessAuthorityPolicy authority) => new(
        ExpectedExecutableSha256,
        authority.ComputeFingerprint(),
        RaceFreeJobAssignmentVerified: true,
        AppContainerIdentityVerified: true,
        RestrictingSidVerified: true,
        WorkspaceAclVerified: true,
        WorkspaceReadSucceeded: true,
        WorkspaceWriteSucceeded: true,
        OutsideReadDenied: true,
        OutsideWriteDenied: true);

    private static WindowsFilesystemIsolationProofEvidence Without(
        WindowsFilesystemIsolationProofEvidence evidence,
        string missing) => missing switch
        {
            "job" => evidence with { RaceFreeJobAssignmentVerified = false },
            "appcontainer" => evidence with { AppContainerIdentityVerified = false },
            "sid" => evidence with { RestrictingSidVerified = false },
            "acl" => evidence with { WorkspaceAclVerified = false },
            "inside-read" => evidence with { WorkspaceReadSucceeded = false },
            "inside-write" => evidence with { WorkspaceWriteSucceeded = false },
            "outside-read" => evidence with { OutsideReadDenied = false },
            "outside-write" => evidence with { OutsideWriteDenied = false },
            _ => throw new ArgumentOutOfRangeException(nameof(missing))
        };
}
