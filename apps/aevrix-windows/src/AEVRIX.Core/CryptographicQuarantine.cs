namespace Aevrix.Core;

public enum CryptographicAccessMode
{
    AnalyzeOnly,
    DecryptWithVaultMaterial,
    ValidateRememberedCandidates
}

public enum QuantumCryptanalysisUse
{
    Disabled,
    SyntheticBenchmarkOnly
}

public sealed record CryptographicQuarantineRequest(
    Guid ProjectId,
    string ArtifactSha256,
    string AuthorizationEvidenceId,
    CryptographicAccessMode Mode,
    string? VaultMaterialReference,
    int RememberedCandidateCount,
    QuantumCryptanalysisUse QuantumUse,
    bool NetworkAllowed,
    bool ExecutionAllowed,
    bool PlaintextPromotionRequiresJudge)
{
    public const int MaxRememberedCandidates = 32;

    public static CryptographicQuarantineRequest Create(
        Guid projectId,
        string artifactSha256,
        string authorizationEvidenceId,
        CryptographicAccessMode mode,
        string? vaultMaterialReference = null,
        int rememberedCandidateCount = 0,
        QuantumCryptanalysisUse quantumUse = QuantumCryptanalysisUse.Disabled)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Cryptographic quarantine requires a project id.", nameof(projectId));

        ArgumentException.ThrowIfNullOrWhiteSpace(artifactSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationEvidenceId);

        var normalizedHash = artifactSha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(static ch => !char.IsAsciiHexDigit(ch)))
            throw new ArgumentException("Artifact SHA-256 must contain exactly 64 hexadecimal characters.", nameof(artifactSha256));

        if (quantumUse is not QuantumCryptanalysisUse.Disabled)
            throw new ArgumentException("Production artifact access cannot use quantum cryptanalysis; quantum work is restricted to synthetic benchmark fixtures.", nameof(quantumUse));

        switch (mode)
        {
            case CryptographicAccessMode.AnalyzeOnly:
                if (!string.IsNullOrWhiteSpace(vaultMaterialReference) || rememberedCandidateCount != 0)
                    throw new ArgumentException("Analyze-only mode cannot receive cryptographic access material.");
                break;

            case CryptographicAccessMode.DecryptWithVaultMaterial:
                ArgumentException.ThrowIfNullOrWhiteSpace(vaultMaterialReference);
                if (rememberedCandidateCount != 0)
                    throw new ArgumentException("Vault-material decryption cannot also validate remembered candidates.");
                break;

            case CryptographicAccessMode.ValidateRememberedCandidates:
                if (!string.IsNullOrWhiteSpace(vaultMaterialReference))
                    throw new ArgumentException("Remembered-candidate validation does not accept a vault material reference.");
                if (rememberedCandidateCount is < 1 or > MaxRememberedCandidates)
                    throw new ArgumentOutOfRangeException(nameof(rememberedCandidateCount), $"Remembered candidate validation is bounded to 1..{MaxRememberedCandidates} user-supplied candidates.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return new CryptographicQuarantineRequest(
            projectId,
            normalizedHash,
            authorizationEvidenceId.Trim(),
            mode,
            string.IsNullOrWhiteSpace(vaultMaterialReference) ? null : vaultMaterialReference.Trim(),
            rememberedCandidateCount,
            QuantumCryptanalysisUse.Disabled,
            NetworkAllowed: false,
            ExecutionAllowed: false,
            PlaintextPromotionRequiresJudge: true);
    }
}
