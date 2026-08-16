namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class BlueprintExecutionProvenanceBinderTests
{
    [TestMethod]
    public void Bind_ExactSuccessfulExecutions_ProducesLedgerAnchoredClosure()
    {
        var projectId = Guid.NewGuid();
        const string missionId = "mission-alpha";
        const string targetId = "target-app";
        var ledger = new ExecutionProofLedger();

        var evidence = new[]
        {
            Observation("ev-static", projectId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis),
            Observation("ev-dynamic", projectId, targetId, "task-dynamic", MissionSpecialistKind.DynamicAnalysis)
        };
        AppendSuccessful(ledger, projectId, missionId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis, 'a');
        AppendSuccessful(ledger, projectId, missionId, targetId, "task-dynamic", MissionSpecialistKind.DynamicAnalysis, 'b');

        var requirement = Requirement(projectId, targetId, ["ev-static", "ev-dynamic"]);
        var bound = new BlueprintExecutionProvenanceBinder().Bind(
            requirement, missionId, evidence, ledger.Snapshot(), ledger.Head);

        Assert.AreEqual(ledger.Head, bound.LedgerHead);
        Assert.AreEqual(2, bound.EvidenceExecutionProofs.Count);
        Assert.AreEqual(64, bound.ProvenanceDigestSha256.Length);
        Assert.IsTrue(bound.EvidenceExecutionProofs.All(item => item.CompletedRecordHashSha256.Length == 64));
    }

    [TestMethod]
    public void Bind_EvidenceFromAnotherMission_IsRejected()
    {
        var projectId = Guid.NewGuid();
        const string targetId = "target-app";
        var ledger = new ExecutionProofLedger();
        var evidence = Observation("ev-static", projectId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis);
        AppendSuccessful(ledger, projectId, "mission-other", targetId, "task-static", MissionSpecialistKind.StaticAnalysis, 'c');

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new BlueprintExecutionProvenanceBinder().Bind(
                Requirement(projectId, targetId, ["ev-static"]),
                "mission-alpha",
                [evidence],
                ledger.Snapshot(),
                ledger.Head));
    }

    [TestMethod]
    public void Bind_FailedExecution_IsRejected()
    {
        var projectId = Guid.NewGuid();
        const string missionId = "mission-alpha";
        const string targetId = "target-app";
        var ledger = new ExecutionProofLedger();
        var evidence = Observation("ev-static", projectId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis);
        AppendFailed(ledger, projectId, missionId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            new BlueprintExecutionProvenanceBinder().Bind(
                Requirement(projectId, targetId, ["ev-static"]),
                missionId,
                [evidence],
                ledger.Snapshot(),
                ledger.Head));
    }

    [TestMethod]
    public void Bind_PersonalDataEvidence_IsRejected()
    {
        var projectId = Guid.NewGuid();
        const string missionId = "mission-alpha";
        const string targetId = "target-app";
        var ledger = new ExecutionProofLedger();
        var evidence = Observation(
            "ev-static", projectId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis,
            EvidenceSensitivity.PersonalData, containsPersonalData: true);
        AppendSuccessful(ledger, projectId, missionId, targetId, "task-static", MissionSpecialistKind.StaticAnalysis, 'd');

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new BlueprintExecutionProvenanceBinder().Bind(
                Requirement(projectId, targetId, ["ev-static"]),
                missionId,
                [evidence],
                ledger.Snapshot(),
                ledger.Head));
    }

    private static BlueprintKnowledgeRequirement Requirement(Guid projectId, string targetId, IReadOnlyList<string> ids) =>
        new("BKR-proof-closure", projectId, targetId, "runtime.framework", "Framework is independently observed.",
            EvidenceObservationClass.Observed, EvidenceSensitivity.ProjectConfidential,
            BlueprintKnowledgePromotionLevel.Reconstructable, 0.95, ids, "knowledge-proof", "validation-proof");

    private static EvidenceObservation Observation(
        string evidenceId,
        Guid projectId,
        string targetId,
        string taskId,
        MissionSpecialistKind specialist,
        EvidenceSensitivity sensitivity = EvidenceSensitivity.ProjectConfidential,
        bool containsPersonalData = false) =>
        new(evidenceId, projectId, targetId, taskId, specialist, EvidenceObservationClass.Observed, sensitivity,
            "runtime.framework", "dotnet", "Observed runtime framework.", 0.95, Hash('e'),
            DateTimeOffset.Parse("2026-08-15T22:00:00Z"), ["artifact-ref"], ["parent-evidence"],
            containsPersonalData, false);

    private static void AppendSuccessful(
        ExecutionProofLedger ledger, Guid projectId, string missionId, string targetId,
        string taskId, MissionSpecialistKind specialist, char resultFill)
    {
        var executionId = MissionExecutionProofIdentity.CreateExecutionId(projectId, missionId, targetId, taskId, specialist);
        ledger.Append(Event(projectId, missionId, executionId, specialist, ExecutionProofStage.Started,
            ExecutionProofOutcome.Pending, "start-" + taskId, result: null));
        ledger.Append(Event(projectId, missionId, executionId, specialist, ExecutionProofStage.Completed,
            ExecutionProofOutcome.Succeeded, "done-" + taskId, result: Hash(resultFill)));
    }

    private static void AppendFailed(
        ExecutionProofLedger ledger, Guid projectId, string missionId, string targetId,
        string taskId, MissionSpecialistKind specialist)
    {
        var executionId = MissionExecutionProofIdentity.CreateExecutionId(projectId, missionId, targetId, taskId, specialist);
        ledger.Append(Event(projectId, missionId, executionId, specialist, ExecutionProofStage.Started,
            ExecutionProofOutcome.Pending, "start-" + taskId, result: null));
        ledger.Append(Event(projectId, missionId, executionId, specialist, ExecutionProofStage.Completed,
            ExecutionProofOutcome.Failed, "done-" + taskId, result: Hash('f')));
    }

    private static ExecutionProofEvent Event(
        Guid projectId, string missionId, string executionId, MissionSpecialistKind specialist,
        ExecutionProofStage stage, ExecutionProofOutcome outcome, string eventId, string? result) =>
        new(eventId, projectId, missionId, executionId, stage, "mission-specialist", specialist.ToString(), outcome,
            Hash('1'), Hash('2'), result, null, null, null, null, null, null,
            DateTimeOffset.Parse("2026-08-15T22:00:00Z"));

    private static string Hash(char fill) => new(fill, 64);
}
