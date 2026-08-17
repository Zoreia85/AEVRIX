using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class CryptographicQuarantineTests
{
    private static readonly string ArtifactHash = new('a', 64);

    [TestMethod]
    public void VaultMaterialDecryptionKeepsExecutionAndNetworkDisabled()
    {
        var request = CryptographicQuarantineRequest.Create(
            Guid.NewGuid(),
            ArtifactHash,
            "authorization-evidence-1",
            CryptographicAccessMode.DecryptWithVaultMaterial,
            vaultMaterialReference: "vault://project/material-1");

        Assert.IsFalse(request.NetworkAllowed);
        Assert.IsFalse(request.ExecutionAllowed);
        Assert.IsTrue(request.PlaintextPromotionRequiresJudge);
        Assert.AreEqual(QuantumCryptanalysisUse.Disabled, request.QuantumUse);
    }

    [TestMethod]
    public void RememberedCandidateValidationIsStrictlyBounded()
    {
        var request = CryptographicQuarantineRequest.Create(
            Guid.NewGuid(),
            ArtifactHash,
            "authorization-evidence-2",
            CryptographicAccessMode.ValidateRememberedCandidates,
            rememberedCandidateCount: CryptographicQuarantineRequest.MaxRememberedCandidates);

        Assert.AreEqual(CryptographicQuarantineRequest.MaxRememberedCandidates, request.RememberedCandidateCount);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            CryptographicQuarantineRequest.Create(
                Guid.NewGuid(),
                ArtifactHash,
                "authorization-evidence-2",
                CryptographicAccessMode.ValidateRememberedCandidates,
                rememberedCandidateCount: CryptographicQuarantineRequest.MaxRememberedCandidates + 1));
    }

    [TestMethod]
    public void ProductionCryptographicRequestRejectsQuantumCryptanalysis()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CryptographicQuarantineRequest.Create(
                Guid.NewGuid(),
                ArtifactHash,
                "authorization-evidence-3",
                CryptographicAccessMode.AnalyzeOnly,
                quantumUse: QuantumCryptanalysisUse.SyntheticBenchmarkOnly));
    }

    [TestMethod]
    public void CryptographicRequestRequiresSha256AndAuthorizationEvidence()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            CryptographicQuarantineRequest.Create(
                Guid.NewGuid(),
                "not-a-sha256",
                "authorization-evidence-4",
                CryptographicAccessMode.AnalyzeOnly));

        Assert.ThrowsExactly<ArgumentException>(() =>
            CryptographicQuarantineRequest.Create(
                Guid.NewGuid(),
                ArtifactHash,
                " ",
                CryptographicAccessMode.AnalyzeOnly));
    }
}
