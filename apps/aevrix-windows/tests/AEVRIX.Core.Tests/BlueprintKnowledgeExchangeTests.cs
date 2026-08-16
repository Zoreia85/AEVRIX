using System.Text;
using System.Text.Json.Nodes;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class BlueprintKnowledgeExchangeTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string MissionId = "mission-exchange-001";

    [TestMethod]
    public void ExportImport_RoundTripsGovernedRequirementAcrossAssemblies()
    {
        var bound = BoundRequirement();
        var requirement = bound.Requirement;
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(bound);

        var imported = new BlueprintKnowledgeExchangeImporter().Import(bytes, ProjectId, "target-001");

        Assert.AreEqual(requirement.RequirementId, imported.RequirementId);
        Assert.AreEqual(requirement.ProjectId, imported.ProjectId);
        Assert.AreEqual(requirement.TargetId, imported.TargetId);
        Assert.AreEqual(requirement.ClaimKey, imported.ClaimKey);
        Assert.AreEqual(requirement.Statement, imported.Statement);
        Assert.AreEqual(BlueprintKnowledgeExchangeBasis.Observed, imported.Basis);
        Assert.AreEqual(BlueprintKnowledgeExchangePromotion.Reconstructable, imported.Promotion);
        Assert.IsTrue(imported.CanDriveReconstruction);
        CollectionAssert.AreEqual(new[] { "obs-a", "obs-b" }, imported.EvidenceIds.ToArray());
        Assert.AreEqual(MissionId, imported.MissionId);
        Assert.AreEqual(bound.LedgerHead.EntryCount, imported.LedgerEntryCount);
        Assert.AreEqual(bound.LedgerHead.HeadHashSha256, imported.LedgerHeadHashSha256);
        Assert.AreEqual(bound.ProvenanceDigestSha256, imported.ProvenanceDigestSha256);
        Assert.AreEqual(2, imported.EvidenceExecutionProofs.Count);
        Assert.AreEqual(64, imported.PayloadSha256.Length);
    }

    [TestMethod]
    public void Import_RejectsPayloadMutationEvenWhenJsonRemainsValid()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(BoundRequirement());
        var node = JsonNode.Parse(bytes)!.AsObject();
        node["requirement"]!["statement"] = "tampered statement";
        var tampered = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            new BlueprintKnowledgeExchangeImporter().Import(tampered, ProjectId, "target-001"));
    }

    [TestMethod]
    public void Import_RejectsProvenanceMutationEvenWhenJsonRemainsValid()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(BoundRequirement());
        var node = JsonNode.Parse(bytes)!.AsObject();
        node["provenance"]!["evidenceExecutionProofs"]![0]!["resultDigestSha256"] = new string('f', 64);
        var tampered = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            new BlueprintKnowledgeExchangeImporter().Import(tampered, ProjectId, "target-001"));
    }

    [TestMethod]
    public void Export_RejectsForgedProofBoundRequirement()
    {
        var bound = BoundRequirement();
        var forged = bound with
        {
            EvidenceExecutionProofs = bound.EvidenceExecutionProofs
                .Select((proof, index) => index == 0
                    ? proof with { ExecutionId = "mission-task:" + new string('0', 64) }
                    : proof)
                .ToArray()
        };

        Assert.Throws<InvalidDataException>(() =>
            new BlueprintKnowledgeExchangeExporter().Export(forged));
    }

    [TestMethod]
    public void Import_RejectsCrossProjectOrTargetReplay()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(BoundRequirement());
        var importer = new BlueprintKnowledgeExchangeImporter();

        Assert.Throws<InvalidDataException>(() =>
            importer.Import(bytes, Guid.Parse("22222222-2222-2222-2222-222222222222"), "target-001"));
        Assert.Throws<InvalidDataException>(() =>
            importer.Import(bytes, ProjectId, "target-002"));
    }

    [TestMethod]
    public void Export_RejectsPersonalDataBoundary()
    {
        var bound = BoundRequirement(EvidenceSensitivity.PersonalData);

        Assert.Throws<InvalidOperationException>(() =>
            new BlueprintKnowledgeExchangeExporter().Export(bound));
    }

    [TestMethod]
    public void ImportSet_IsIdempotentForIdenticalRequirement()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(BoundRequirement());
        var documents = new[] { (ReadOnlyMemory<byte>)bytes, (ReadOnlyMemory<byte>)bytes };

        var imported = new BlueprintKnowledgeExchangeImporter().ImportSet(documents, ProjectId, "target-001");

        Assert.AreEqual(1, imported.Count);
    }

    private static ProofBoundBlueprintKnowledgeRequirement BoundRequirement(
        EvidenceSensitivity requirementSensitivity = EvidenceSensitivity.ProjectConfidential)
    {
        var requirement = Requirement() with { Sensitivity = requirementSensitivity };
        var observations = new[]
        {
            Observation("obs-a", "task-static", MissionSpecialistKind.StaticAnalysis, "dotnet"),
            Observation("obs-b", "task-dynamic", MissionSpecialistKind.DynamicAnalysis, "aspnet")
        };
        var ledger = new ExecutionProofLedger();
        foreach (var observation in observations)
            AppendSuccessfulExecution(ledger, observation);

        return new BlueprintExecutionProvenanceBinder().Bind(
            requirement,
            MissionId,
            observations,
            ledger.Snapshot(),
            ledger.Head);
    }

    private static EvidenceObservation Observation(
        string evidenceId,
        string taskId,
        MissionSpecialistKind specialist,
        string value) => new(
            evidenceId,
            ProjectId,
            "target-001",
            taskId,
            specialist,
            EvidenceObservationClass.Observed,
            EvidenceSensitivity.ProjectConfidential,
            "runtime.framework",
            value,
            "Sanitized governed observation.",
            0.95,
            new string('a', 64),
            DateTimeOffset.Parse("2026-08-16T03:00:00Z"),
            ["artifact-ref"],
            [],
            ContainsPersonalData: false);

    private static void AppendSuccessfulExecution(ExecutionProofLedger ledger, EvidenceObservation observation)
    {
        var executionId = MissionExecutionProofIdentity.CreateExecutionId(
            ProjectId,
            MissionId,
            "target-001",
            observation.SourceTaskId,
            observation.Specialist);
        var inputDigest = new string('b', 64);
        var resultDigest = observation.Specialist == MissionSpecialistKind.StaticAnalysis
            ? new string('c', 64)
            : new string('d', 64);
        var timestamp = DateTimeOffset.Parse("2026-08-16T03:01:00Z");

        ledger.Append(new ExecutionProofEvent(
            $"evt-start-{observation.SourceTaskId}",
            ProjectId,
            MissionId,
            executionId,
            ExecutionProofStage.Started,
            "mission-specialist",
            observation.Specialist.ToString(),
            ExecutionProofOutcome.Pending,
            inputDigest,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            timestamp));

        ledger.Append(new ExecutionProofEvent(
            $"evt-done-{observation.SourceTaskId}",
            ProjectId,
            MissionId,
            executionId,
            ExecutionProofStage.Completed,
            "mission-specialist",
            observation.Specialist.ToString(),
            ExecutionProofOutcome.Succeeded,
            inputDigest,
            null,
            resultDigest,
            null,
            null,
            null,
            null,
            null,
            null,
            timestamp.AddSeconds(1)));
    }

    private static BlueprintKnowledgeRequirement Requirement() => new(
        RequirementId: "BKR-0123456789abcdef0123456789abcdef",
        ProjectId: ProjectId,
        TargetId: "target-001",
        ClaimKey: "runtime.framework",
        Statement: "runtime.framework = ASP.NET Core",
        Basis: EvidenceObservationClass.Observed,
        Sensitivity: EvidenceSensitivity.ProjectConfidential,
        PromotionLevel: BlueprintKnowledgePromotionLevel.Reconstructable,
        Confidence: 0.94,
        EvidenceIds: ["obs-b", "obs-a"],
        SourceKnowledgeId: "KN-0123456789abcdef0123456789abcdef",
        ValidationRecordId: "VR-0123456789abcdef0123456789abcdef");
}
