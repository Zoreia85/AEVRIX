using System.Security.Cryptography;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Exact hostile-probe evidence required before AEVRIX may claim a Windows WorkspaceOnly
/// filesystem boundary. This is evidence only: it does not select a backend and grants no
/// execution authority by itself.
/// </summary>
public sealed record WindowsFilesystemIsolationProofEvidence(
    string ExecutableSha256,
    string AuthorityFingerprint,
    bool RaceFreeJobAssignmentVerified,
    bool AppContainerIdentityVerified,
    bool RestrictingSidVerified,
    bool WorkspaceAclVerified,
    bool WorkspaceReadSucceeded,
    bool WorkspaceWriteSucceeded,
    bool OutsideReadDenied,
    bool OutsideWriteDenied)
{
    public WindowsFilesystemIsolationProofEvidence Validate()
    {
        ValidateSha256(ExecutableSha256, nameof(ExecutableSha256));
        ValidateSha256(AuthorityFingerprint, nameof(AuthorityFingerprint));
        return this;
    }

    internal static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 value must contain exactly 64 hexadecimal characters.", parameterName);
        }
    }
}

public sealed record WindowsFilesystemIsolationProofDecision(
    bool ExecutableIdentityBound,
    bool AuthorityPolicyBound,
    bool FilesystemWriteBoundaryEnforced,
    bool FilesystemReadIsolationEnforced,
    bool WorkspaceOnlyIsolationProven,
    string DecisionCode)
{
    public WindowsFilesystemIsolationProofDecision Validate()
    {
        if (string.IsNullOrWhiteSpace(DecisionCode)
            || DecisionCode.Length > 128
            || DecisionCode.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Filesystem-isolation proof decision code is invalid.");
        }

        if (WorkspaceOnlyIsolationProven
            && (!ExecutableIdentityBound
                || !AuthorityPolicyBound
                || !FilesystemWriteBoundaryEnforced
                || !FilesystemReadIsolationEnforced))
        {
            throw new InvalidDataException("WorkspaceOnly proof cannot be true without every exact binding and read/write boundary proof.");
        }

        return this;
    }
}

/// <summary>
/// Fail-closed proof gate for true Windows WorkspaceOnly isolation.
///
/// Write-boundary evidence and read-isolation evidence are deliberately evaluated separately.
/// A successful external-write denial must never be promoted into a read-isolation claim.
/// WorkspaceOnly is proven only when the exact pinned executable identity, exact authority-policy
/// fingerprint, race-free Job Object assignment, AppContainer identity, restricting SID, governed
/// workspace ACL and all four hostile in/out read/write observations are proven together.
///
/// This gate does not wire a backend into WindowsIsolationBackendSelector. That remains a later,
/// separately reviewed step after hostile Windows execution supplies a complete proof bundle.
/// </summary>
public sealed class WindowsFilesystemIsolationProofGate
{
    public WindowsFilesystemIsolationProofDecision EvaluateWorkspaceOnly(
        WindowsFilesystemIsolationProofEvidence evidence,
        string expectedExecutableSha256,
        OutOfProcessAuthorityPolicy expectedAuthority)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(expectedAuthority);
        evidence.Validate();
        WindowsFilesystemIsolationProofEvidence.ValidateSha256(
            expectedExecutableSha256,
            nameof(expectedExecutableSha256));
        expectedAuthority.Validate();

        if (expectedAuthority.Filesystem.Scope != OutOfProcessFilesystemScope.WorkspaceOnly)
        {
            return Decision(false, false, false, false, false, "WorkspaceOnlyAuthorityRequired");
        }

        var executableBound = FixedTimeSha256Equals(evidence.ExecutableSha256, expectedExecutableSha256);
        if (!executableBound)
        {
            return Decision(false, false, false, false, false, "ExecutableSha256Mismatch");
        }

        var expectedFingerprint = expectedAuthority.ComputeFingerprint();
        var authorityBound = FixedTimeSha256Equals(evidence.AuthorityFingerprint, expectedFingerprint);
        if (!authorityBound)
        {
            return Decision(true, false, false, false, false, "AuthorityFingerprintMismatch");
        }

        var commonBoundary = evidence.RaceFreeJobAssignmentVerified
            && evidence.AppContainerIdentityVerified
            && evidence.RestrictingSidVerified
            && evidence.WorkspaceAclVerified;

        var writeBoundary = commonBoundary
            && evidence.WorkspaceWriteSucceeded
            && evidence.OutsideWriteDenied;

        var readIsolation = commonBoundary
            && evidence.WorkspaceReadSucceeded
            && evidence.OutsideReadDenied;

        var workspaceOnly = writeBoundary && readIsolation;
        var code = workspaceOnly
            ? "WorkspaceOnlyIsolationProven"
            : FirstMissingEvidenceCode(evidence);

        return Decision(
            true,
            true,
            writeBoundary,
            readIsolation,
            workspaceOnly,
            code);
    }

    private static string FirstMissingEvidenceCode(WindowsFilesystemIsolationProofEvidence evidence)
    {
        if (!evidence.RaceFreeJobAssignmentVerified)
        {
            return "RaceFreeJobAssignmentUnproven";
        }
        if (!evidence.AppContainerIdentityVerified)
        {
            return "AppContainerIdentityUnproven";
        }
        if (!evidence.RestrictingSidVerified)
        {
            return "RestrictingSidUnproven";
        }
        if (!evidence.WorkspaceAclVerified)
        {
            return "WorkspaceAclUnproven";
        }
        if (!evidence.WorkspaceReadSucceeded)
        {
            return "WorkspaceReadUnproven";
        }
        if (!evidence.WorkspaceWriteSucceeded)
        {
            return "WorkspaceWriteUnproven";
        }
        if (!evidence.OutsideReadDenied)
        {
            return "OutsideReadNotDenied";
        }
        if (!evidence.OutsideWriteDenied)
        {
            return "OutsideWriteNotDenied";
        }

        return "WorkspaceOnlyIsolationUnproven";
    }

    private static WindowsFilesystemIsolationProofDecision Decision(
        bool executableIdentityBound,
        bool authorityPolicyBound,
        bool filesystemWriteBoundaryEnforced,
        bool filesystemReadIsolationEnforced,
        bool workspaceOnlyIsolationProven,
        string decisionCode) =>
        new WindowsFilesystemIsolationProofDecision(
            executableIdentityBound,
            authorityPolicyBound,
            filesystemWriteBoundaryEnforced,
            filesystemReadIsolationEnforced,
            workspaceOnlyIsolationProven,
            decisionCode).Validate();

    private static bool FixedTimeSha256Equals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
