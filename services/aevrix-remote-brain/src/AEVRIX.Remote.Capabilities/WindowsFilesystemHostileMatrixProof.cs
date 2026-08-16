namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Evidence-only contract for a Windows filesystem-isolation claim. It deliberately does not infer
/// read isolation from AppContainer presence, restricted tokens, Low Integrity, workspace ACLs,
/// working-directory containment, or denied external writes. Every hostile boundary must be observed.
/// </summary>
public sealed record WindowsFilesystemHostileMatrixProof(
    bool ControlledExternalReadDenied,
    bool UserProfileReadDenied,
    bool RepositorySourceTreeReadDenied,
    bool TempReadDenied,
    bool SiblingWorkspaceReadDenied,
    bool ReparseEscapeDenied,
    bool ControlledExternalWriteDenied,
    bool SiblingWorkspaceWriteDenied,
    bool InWorkspaceReadSucceeded,
    bool InWorkspaceWriteSucceeded,
    bool InWorkspaceRuntimeLoadSucceeded,
    string AuthorityFingerprint)
{
    public WindowsFilesystemHostileMatrixProof Validate()
    {
        if (string.IsNullOrWhiteSpace(AuthorityFingerprint)
            || AuthorityFingerprint.Length != 64
            || !AuthorityFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Hostile filesystem matrix requires an exact SHA-256 authority fingerprint.",
                nameof(AuthorityFingerprint));
        }
        return this;
    }

    public bool ReadIsolationProven =>
        ControlledExternalReadDenied
        && UserProfileReadDenied
        && RepositorySourceTreeReadDenied
        && TempReadDenied
        && SiblingWorkspaceReadDenied
        && ReparseEscapeDenied;

    public bool WriteBoundaryProven =>
        ControlledExternalWriteDenied
        && SiblingWorkspaceWriteDenied
        && ReparseEscapeDenied;

    public bool WorkspaceCompatibilityProven =>
        InWorkspaceReadSucceeded
        && InWorkspaceWriteSucceeded
        && InWorkspaceRuntimeLoadSucceeded;

    public bool IsComplete =>
        ReadIsolationProven
        && WriteBoundaryProven
        && WorkspaceCompatibilityProven;
}

public static class WindowsFilesystemHostileMatrixGate
{
    public static WindowsFilesystemHostileMatrixProof RequireComplete(
        WindowsFilesystemHostileMatrixProof proof,
        string expectedAuthorityFingerprint)
    {
        ArgumentNullException.ThrowIfNull(proof);
        proof.Validate();

        if (string.IsNullOrWhiteSpace(expectedAuthorityFingerprint)
            || expectedAuthorityFingerprint.Length != 64
            || !expectedAuthorityFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Expected authority fingerprint must be an exact SHA-256 value.",
                nameof(expectedAuthorityFingerprint));
        }

        if (!string.Equals(
                proof.AuthorityFingerprint,
                expectedAuthorityFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Hostile filesystem matrix is bound to a different authority policy.");
        }

        if (!proof.ReadIsolationProven)
        {
            throw new InvalidDataException(
                "Windows filesystem read isolation is not proven by the complete hostile matrix.");
        }

        if (!proof.WriteBoundaryProven)
        {
            throw new InvalidDataException(
                "Windows filesystem write boundary is not proven by the complete hostile matrix.");
        }

        if (!proof.WorkspaceCompatibilityProven)
        {
            throw new InvalidDataException(
                "Windows filesystem isolation breaks required in-workspace runtime compatibility.");
        }

        return proof;
    }
}

/// <summary>
/// The only helper that upgrades hostile observations into a granular filesystem authority binding.
/// A backend still has to provide the execution attestation; this factory only mints read/write proof
/// flags after the complete hostile matrix is bound to the exact authority policy.
/// </summary>
public static class WindowsFilesystemIsolationAttestationFactory
{
    public static IsolationAuthorityAttestation Create(
        string backendId,
        OutOfProcessAuthorityPolicy authority,
        WindowsFilesystemHostileMatrixProof proof)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(proof);
        authority.Validate();

        if (!authority.Filesystem.RequiresIsolation)
        {
            throw new InvalidOperationException(
                "Hostile filesystem attestation is only valid for a filesystem-restricted authority profile.");
        }

        var fingerprint = authority.ComputeFingerprint();
        var accepted = WindowsFilesystemHostileMatrixGate.RequireComplete(proof, fingerprint);

        return new IsolationAuthorityAttestation(
            backendId,
            fingerprint,
            FilesystemWriteBoundaryEnforced: accepted.WriteBoundaryProven,
            FilesystemReadIsolationEnforced: accepted.ReadIsolationProven).Validate();
    }
}
