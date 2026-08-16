using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class QirMissionHintAdvisorTests
{
    [TestMethod]
    public void BuildHints_RanksSpecialistsFromPromotedPatterns()
    {
        var advisor = Advisor();
        var hints = advisor.BuildHints([
            Pattern("QIR-a", "pattern.binary.protocol", 3, 0.96),
            Pattern("QIR-b", "pattern.visual.state", 4, 0.90)
        ]);

        Assert.AreEqual(3, hints.Count);
        Assert.AreEqual(MissionSpecialistKind.StaticAnalysis, hints[0].Specialist);
        Assert.IsTrue(hints[0].PriorityScore > hints[1].PriorityScore);
    }

    [TestMethod]
    public void BuildHints_DoesNotTreatHintsAsEvidenceOrBlueprintAuthority()
    {
        var hint = Advisor().BuildHints([Pattern("QIR-a", "pattern.binary.protocol", 3, 0.96)]).Single(x => x.Specialist == MissionSpecialistKind.StaticAnalysis);

        Assert.IsFalse(hint.IsEvidence);
        Assert.IsFalse(hint.CanSatisfyEvidenceRequirement);
        Assert.IsFalse(hint.CanDriveBlueprint);
        CollectionAssert.AreEqual(new[] { "QIR-a" }, hint.PatternIds.ToArray());
    }

    [TestMethod]
    public void BuildHints_RejectsWeakOrSingleProjectPatterns()
    {
        var advisor = Advisor();
        var hints = advisor.BuildHints([
            Pattern("QIR-weak", "pattern.binary.protocol", 4, 0.70),
            Pattern("QIR-single", "pattern.visual.state", 1, 0.99)
        ]);

        Assert.AreEqual(0, hints.Count);
    }

    [TestMethod]
    public void BuildHints_IgnoresUnknownPatternsInsteadOfInventingRouting()
    {
        var hints = Advisor().BuildHints([Pattern("QIR-x", "pattern.unknown", 8, 0.99)]);
        Assert.AreEqual(0, hints.Count);
    }

    [TestMethod]
    public void BuildHints_IsBoundedAndDeterministic()
    {
        var advisor = new QirMissionHintAdvisor([
            new("pattern.one", MissionSpecialistKind.StaticAnalysis, 1.00, "static-priority"),
            new("pattern.two", MissionSpecialistKind.DynamicAnalysis, 0.95, "dynamic-priority"),
            new("pattern.three", MissionSpecialistKind.NetworkBehavior, 0.90, "network-priority")
        ], new QirMissionHintPolicy(MaximumHints: 2));

        var hints = advisor.BuildHints([
            Pattern("QIR-3", "pattern.three", 3, 0.95),
            Pattern("QIR-1", "pattern.one", 3, 0.95),
            Pattern("QIR-2", "pattern.two", 3, 0.95)
        ]);

        Assert.AreEqual(2, hints.Count);
        Assert.AreEqual(MissionSpecialistKind.StaticAnalysis, hints[0].Specialist);
        Assert.AreEqual(MissionSpecialistKind.DynamicAnalysis, hints[1].Specialist);
    }

    [TestMethod]
    public void Constructor_RejectsInvalidRoutingRule()
    {
        Assert.Throws<InvalidDataException>(() => new QirMissionHintAdvisor([
            new QirRoutingRule("pattern.ok", MissionSpecialistKind.StaticAnalysis, 0, "bad-weight")
        ]));
    }

    private static QirMissionHintAdvisor Advisor() => new([
        new("pattern.binary.protocol", MissionSpecialistKind.StaticAnalysis, 1.00, "inspect-structure"),
        new("pattern.binary.protocol", MissionSpecialistKind.DynamicAnalysis, 0.80, "validate-runtime"),
        new("pattern.visual.state", MissionSpecialistKind.VisionOcr, 0.90, "inspect-visual-state")
    ]);

    private static QirGlobalPattern Pattern(string id, string key, int projects, double confidence) =>
        new(
            id,
            key,
            new string('a', 64),
            projects,
            Math.Max(projects, projects * 2),
            confidence,
            new DateTimeOffset(2026, 8, 14, 23, 30, 0, TimeSpan.Zero));
}
