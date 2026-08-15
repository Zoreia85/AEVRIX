using System.Text;
using System.Text.Json.Nodes;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class BlueprintKnowledgeExchangeTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public void ExportImport_RoundTripsGovernedRequirementAcrossAssemblies()
    {
        var requirement = Requirement();
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(requirement);

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
        Assert.AreEqual(64, imported.PayloadSha256.Length);
    }

    [TestMethod]
    public void Import_RejectsPayloadMutationEvenWhenJsonRemainsValid()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(Requirement());
        var node = JsonNode.Parse(bytes)!.AsObject();
        node["requirement"]!["statement"] = "tampered statement";
        var tampered = Encoding.UTF8.GetBytes(node.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            new BlueprintKnowledgeExchangeImporter().Import(tampered, ProjectId, "target-001"));
    }

    [TestMethod]
    public void Import_RejectsCrossProjectOrTargetReplay()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(Requirement());
        var importer = new BlueprintKnowledgeExchangeImporter();

        Assert.Throws<InvalidDataException>(() =>
            importer.Import(bytes, Guid.Parse("22222222-2222-2222-2222-222222222222"), "target-001"));
        Assert.Throws<InvalidDataException>(() =>
            importer.Import(bytes, ProjectId, "target-002"));
    }

    [TestMethod]
    public void Export_RejectsPersonalDataBoundary()
    {
        var requirement = Requirement() with { Sensitivity = EvidenceSensitivity.PersonalData };

        Assert.Throws<InvalidOperationException>(() =>
            new BlueprintKnowledgeExchangeExporter().Export(requirement));
    }

    [TestMethod]
    public void ImportSet_IsIdempotentForIdenticalRequirement()
    {
        var bytes = new BlueprintKnowledgeExchangeExporter().Export(Requirement());
        var documents = new[] { (ReadOnlyMemory<byte>)bytes, (ReadOnlyMemory<byte>)bytes };

        var imported = new BlueprintKnowledgeExchangeImporter().ImportSet(documents, ProjectId, "target-001");

        Assert.AreEqual(1, imported.Count);
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
