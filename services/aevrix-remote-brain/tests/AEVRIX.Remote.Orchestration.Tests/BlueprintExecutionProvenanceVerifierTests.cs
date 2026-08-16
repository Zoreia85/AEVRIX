using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class BlueprintExecutionProvenanceVerifierTests
{
    private static readonly Guid ProjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string MissionId = "mission-provenance-verify-001";
    private const string TargetId = "target-provenance-001";

    [TestMethod]
    public void Verify_AcceptsBinderProducedRequirement()
    {
        BlueprintExecutionProvenanceBinder.Verify(CreateBoundRequirement());
    }

    [TestMethod]
    public void Verify_RejectsForgedExecutionIdentity()
    {
        var bound = CreateBoundRequirement();
        var forged = bound with
        {
            EvidenceExecutionProofs = bound.EvidenceExecutionProofs
                .Select((proof, index) => index == 0
                    ? proof with { ExecutionId = "mission-task:" + new string('0', 64) }
                    : proof)
                .ToArray()
        };

        Assert.Throws<InvalidDataException>(() => BlueprintExecutionProvenanceBinder.Verify(forged));
    }

    [TestMethod]
    public void Verify_RejectsTamperedProvenanceDigest()
    {
        var bound = CreateBoundRequirement() with { ProvenanceDigestSha256 = new string('0', 64) };

        Assert.Throws<CryptographicException>(() => BlueprintExecutionProvenanceBinder.Verify(bound));
    }

    [TestMethod]
    public void Verify_RejectsTamperedLedgerHead()
    {
        var bound = CreateBoundRequirement();
        var tampered = bound with
        {
            LedgerHead = bound.LedgerHead with { HeadHashSha256 = new string('0', 64) }
        };

        Assert.Throws<CryptographicException>(() => BlueprintExecutionProvenanceBinder.Verify(tampered));
    }

    [TestMethod]
    public void Verify_RejectsDuplicateEvidenceBinding()
    {
        var bound = CreateBoundRequirement();
        var duplicate = bound with
        {
            EvidenceExecutionProofs =
            [
                bound.EvidenceExecutionProofs[0],
                bound.EvidenceExecutionProofs[0]
            ]
        };

        Assert.Throws<InvalidDataException>(() => BlueprintExecutionProvenanceBinder.Verify(duplicate));
    }

    [TestMethod]
    public void Verify_RejectsCrossTargetRebinding()
    {
        var bound = CreateBoundRequirement();
        var rebound = bound with
        {
            Requirement = bound.Requirement with { TargetId = "target-provenance-002" }
        };

        Assert.Throws<InvalidDataException>(() => BlueprintExecutionProvenanceBinder.Verify(rebound));
    }

    private static ProofBoundBlueprintKnowledgeRequirement CreateBoundRequirement()
    {
        var requirement = new BlueprintKnowledgeRequirement(
            RequirementId: "BKR-33333333333333333333333333333333",
            ProjectId: ProjectId,
            TargetId: TargetId,
            ClaimKey: "runtime.framework",
            Statement: "runtime.framework = governed-runtime",
            Basis: EvidenceObservationClass.Observed,
            Sensitivity: EvidenceSensitivity.ProjectConfidential,
            PromotionLevel: BlueprintKnowledgePromotionLevel.Reconstructable,
            Confidence: 0.97,
            EvidenceIds: ["obs-static", "obs-dynamic"],
            SourceKnowledgeId: "KN-33333333333333333333333333333333",
            ValidationRecordId: "VR-33333333333333333333333333333333");

        var observations = new[]
        {
            CreateObservation("obs-static", "task-static", MissionSpecialistKind.StaticAnalysis, "dotnet"),
            CreateObservation("obs-dynamic", "task-dynamic", MissionSpecialistKind.DynamicAnalysis, "aspnet")
        };

        var ledger = new ExecutionProofLedger();
        foreach (var observation in observations)
            AppendSuccessfulExecution(ledger, observation);

        return new BlueprintExecutionProvenanceBinder().Bind(
            requirement, MissionId, observations, ledger.Snapshot(), ledger.Head);
    }

    private static EvidenceObservation CreateObservation(
        string evidenceId,
        string taskId,
        MissionSpecialistKind specialist,
        string value) => new(
            evidenceId,
            ProjectId,
            TargetId,
            taskId,
            specialist,
            EvidenceObservationClass.Observed,
            EvidenceSensitivity.ProjectConfidential,
            "runtime.framework",
            value,
            "Sanitized governed observation.",
            0.97,
            new string('a', 64),
            DateTimeOffset.Parse("2026-08-16T04:00:00Z"),
            ["artifact-ref"],
            [$"parent-{evidenceId}"],
            ContainsPersonalData: false);

    private static void AppendSuccessfulExecution(ExecutionProofLedger ledger, EvidenceObservation observation)
    {
        var executionId = MissionExecutionProofIdentity.CreateExecutionId(
            ProjectId, MissionId, TargetId, observation.SourceTaskId, observation.Specialist);
        var inputDigest = new string('b', 64);
        var resultDigest = observation.Specialist == MissionSpecialistKind.StaticAnalysis
            ? new string('c', 64)
            : new string('d', 64);
        var timestamp = DateTimeOffset.Parse("2026-08-16T04:01:00Z");

        ledger.Append(new ExecutionProofEvent(
            $"evt-start-{observation.SourceTaskId}", ProjectId, MissionId, executionId,
            ExecutionProofStage.Started, "mission-specialist", observation.Specialist.ToString(),
            ExecutionProofOutcome.Pending, inputDigest, null, null, null, null, null, null, null, null, timestamp));

        ledger.Append(new ExecutionProofEvent(
            $"evt-done-{observation.SourceTaskId}", ProjectId, MissionId, executionId,
            ExecutionProofStage.Completed, "mission-specialist", observation.Specialist.ToString(),
            ExecutionProofOutcome.Succeeded, inputDigest, null, resultDigest, null, null, null, null, null, null,
            timestamp.AddSeconds(1)));
    }
}
