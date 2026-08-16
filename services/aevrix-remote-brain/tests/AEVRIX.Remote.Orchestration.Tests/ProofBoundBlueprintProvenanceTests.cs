using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ProofBoundBlueprintProvenanceTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public void ComputeMissionExecutionId_MatchesProofRecordingIdentityContract()
    {
        var actual = BlueprintExecutionProvenanceBinder.ComputeMissionExecutionId(
            ProjectId,
            "mission-001",
            "target-a",
            "task-static",
            MissionSpecialistKind.StaticAnalysis);

        Assert.AreEqual(
            "mission-task:fbc8e7b096129309e6bf3a047fb326b15259e65760a2ac92535015c1ce4a64eb",
            actual);
    }

    [TestMethod]
    public void Bind_SuccessfulSourceExecution_ProducesVerifiableClosure()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation();
        var (records, head) = ProofChain("mission-001", observation, ExecutionProofOutcome.Succeeded);

        var bound = binder.Bind(Requirement(), [observation], "mission-001", records, head);

        Assert.AreEqual(head, bound.LedgerHead);
        Assert.AreEqual(1, bound.EvidenceProvenance.Count);
        Assert.AreEqual("ev-001", bound.EvidenceProvenance[0].EvidenceId);
        Assert.IsTrue(binder.VerifyClosure(bound));
    }

    [TestMethod]
    public void Bind_MissingCompletedSourceProof_IsRejected()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation();
        var executionId = BlueprintExecutionProvenanceBinder.ComputeMissionExecutionId(
            ProjectId,
            "mission-001",
            observation.TargetId,
            observation.SourceTaskId,
            observation.Specialist);
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started("mission-001", executionId, observation.Specialist));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            binder.Bind(Requirement(), [observation], "mission-001", ledger.Snapshot(), ledger.Head));
    }

    [TestMethod]
    public void Bind_FailedSourceExecution_IsRejected()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation();
        var (records, head) = ProofChain("mission-001", observation, ExecutionProofOutcome.Failed);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            binder.Bind(Requirement(), [observation], "mission-001", records, head));
    }

    [TestMethod]
    public void Bind_CrossRunProof_IsRejected()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation();
        var (records, head) = ProofChain("mission-002", observation, ExecutionProofOutcome.Succeeded);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            binder.Bind(Requirement(), [observation], "mission-001", records, head));
    }

    [TestMethod]
    public void Bind_TamperedLedgerHead_IsRejectedBeforeProjection()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation();
        var (records, head) = ProofChain("mission-001", observation, ExecutionProofOutcome.Succeeded);
        var tampered = head with { HeadHashSha256 = new string('a', 64) };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            binder.Bind(Requirement(), [observation], "mission-001", records, tampered));
    }

    [TestMethod]
    public void VerifyClosure_BlueprintStatementMutation_IsDetected()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation();
        var (records, head) = ProofChain("mission-001", observation, ExecutionProofOutcome.Succeeded);
        var bound = binder.Bind(Requirement(), [observation], "mission-001", records, head);
        var mutated = bound with
        {
            Requirement = bound.Requirement with { Statement = "mutated blueprint statement" }
        };

        Assert.IsFalse(binder.VerifyClosure(mutated));
    }

    [TestMethod]
    public void Bind_PersonalDataObservation_IsRejected()
    {
        var binder = new BlueprintExecutionProvenanceBinder();
        var observation = Observation() with
        {
            Sensitivity = EvidenceSensitivity.PersonalData,
            ContainsPersonalData = true
        };
        var (records, head) = ProofChain("mission-001", observation, ExecutionProofOutcome.Succeeded);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            binder.Bind(Requirement(), [observation], "mission-001", records, head));
    }

    private static BlueprintKnowledgeRequirement Requirement() => new(
        RequirementId: "BKR-proof-001",
        ProjectId,
        TargetId: "target-a",
        ClaimKey: "claim-format",
        Statement: "The target exposes a deterministic domain-neutral format contract.",
        Basis: EvidenceObservationClass.Observed,
        Sensitivity: EvidenceSensitivity.ProjectConfidential,
        PromotionLevel: BlueprintKnowledgePromotionLevel.Conditional,
        Confidence: 0.94,
        EvidenceIds: ["ev-001"],
        SourceKnowledgeId: "knowledge-001",
        ValidationRecordId: "validation-001");

    private static EvidenceObservation Observation() => new(
        EvidenceId: "ev-001",
        ProjectId,
        TargetId: "target-a",
        SourceTaskId: "task-static",
        Specialist: MissionSpecialistKind.StaticAnalysis,
        ObservationClass: EvidenceObservationClass.Observed,
        Sensitivity: EvidenceSensitivity.ProjectConfidential,
        ClaimKey: "claim-format",
        ClaimValue: "deterministic",
        Summary: "Observed a deterministic format boundary.",
        Confidence: 0.95,
        ContentSha256: new string('1', 64),
        ObservedAt: DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
        SourceArtifactIds: [],
        ParentEvidenceIds: ["seed-001"]);

    private static (IReadOnlyList<ExecutionProofRecord> Records, ExecutionProofHead Head) ProofChain(
        string runId,
        EvidenceObservation observation,
        ExecutionProofOutcome outcome)
    {
        var executionId = BlueprintExecutionProvenanceBinder.ComputeMissionExecutionId(
            ProjectId,
            runId,
            observation.TargetId,
            observation.SourceTaskId,
            observation.Specialist);
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started(runId, executionId, observation.Specialist));
        ledger.Append(Completed(runId, executionId, observation.Specialist, outcome));
        return (ledger.Snapshot(), ledger.Head);
    }

    private static ExecutionProofEvent Started(
        string runId,
        string executionId,
        MissionSpecialistKind specialist) => new(
        EventId: "proof-start:" + executionId["mission-task:".Length..],
        ProjectId,
        RunId: runId,
        ExecutionId: executionId,
        Stage: ExecutionProofStage.Started,
        CapabilityClass: "mission-specialist",
        CapabilityId: specialist.ToString(),
        Outcome: ExecutionProofOutcome.Pending,
        InputDigestSha256: new string('2', 64),
        AuthorityDigestSha256: new string('3', 64),
        ResultDigestSha256: null,
        AttestationDigestSha256: null,
        ArtifactManifestSha256: null,
        ValidationDigestSha256: null,
        JudgeDecisionDigestSha256: null,
        PromotionDigestSha256: null,
        PromotionReference: null,
        ObservedAt: DateTimeOffset.Parse("2026-08-16T00:00:01Z"));

    private static ExecutionProofEvent Completed(
        string runId,
        string executionId,
        MissionSpecialistKind specialist,
        ExecutionProofOutcome outcome) => new(
        EventId: "proof-complete:" + executionId["mission-task:".Length..],
        ProjectId,
        RunId: runId,
        ExecutionId: executionId,
        Stage: ExecutionProofStage.Completed,
        CapabilityClass: "mission-specialist",
        CapabilityId: specialist.ToString(),
        Outcome: outcome,
        InputDigestSha256: new string('2', 64),
        AuthorityDigestSha256: new string('3', 64),
        ResultDigestSha256: new string('4', 64),
        AttestationDigestSha256: null,
        ArtifactManifestSha256: null,
        ValidationDigestSha256: null,
        JudgeDecisionDigestSha256: null,
        PromotionDigestSha256: null,
        PromotionReference: null,
        ObservedAt: DateTimeOffset.Parse("2026-08-16T00:00:02Z"));
}
