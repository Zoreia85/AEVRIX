using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class QirLearningLedgerTests
{
    [TestMethod]
    public void Promote_RequiresIndependentProjects()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Observation(ProjectA, "obs-a", 0.95));
        ledger.Record(Observation(ProjectA, "obs-b", 0.96));

        Assert.Throws<InvalidOperationException>(() => ledger.Promote("runtime.framework", FeatureHash));
    }

    [TestMethod]
    public void Promote_AggregatesEligiblePublicEvidenceWithoutLeakingProjectIdentifiers()
    {
        var ledger = new QirLearningLedger(timeProvider: new FixedTimeProvider());
        ledger.Record(Observation(ProjectA, "obs-a", 0.95));
        ledger.Record(Observation(ProjectB, "obs-b", 0.91));

        var pattern = ledger.Promote("runtime.framework", FeatureHash);

        Assert.AreEqual(2, pattern.IndependentProjectCount);
        Assert.AreEqual(2, pattern.ObservationCount);
        Assert.AreEqual("runtime.framework", pattern.PatternKey);
        Assert.AreEqual(FeatureHash, pattern.FeatureHash);
        Assert.IsFalse(pattern.ToString().Contains(ProjectA.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(pattern.ToString().Contains(ProjectB.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(pattern.ToString().Contains("ev-", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Promote_IgnoresConfidentialAndInferredObservations()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Observation(ProjectA, "obs-a", 0.95));
        ledger.Record(Observation(ProjectB, "obs-b", 0.95, sensitivity: QirLearningSensitivity.ProjectConfidential));
        ledger.Record(Observation(ProjectC, "obs-c", 0.95, basis: QirLearningBasis.Inferred));

        Assert.Throws<InvalidOperationException>(() => ledger.Promote("runtime.framework", FeatureHash));
    }

    [TestMethod]
    public void Record_RejectsRawSecretMaterial()
    {
        var ledger = new QirLearningLedger();
        var observation = Observation(ProjectA, "obs-secret", 0.95) with { ContainsRawSecretMaterial = true };

        Assert.Throws<InvalidDataException>(() => ledger.Record(observation));
    }

    [TestMethod]
    public void Record_RejectsPublicPersonalData()
    {
        var ledger = new QirLearningLedger();
        var observation = Observation(ProjectA, "obs-pii", 0.95) with { ContainsPersonalData = true };

        Assert.Throws<InvalidDataException>(() => ledger.Record(observation));
    }

    [TestMethod]
    public void Record_IsIdempotentButRejectsObservationIdRebindingWithinProject()
    {
        var ledger = new QirLearningLedger();
        var first = Observation(ProjectA, "obs-fixed", 0.95);

        var stored = ledger.Record(first);
        var repeated = ledger.Record(first);
        Assert.AreSame(stored, repeated);

        var mutated = first with { Confidence = 0.70 };
        Assert.Throws<InvalidOperationException>(() => ledger.Record(mutated));
    }

    [TestMethod]
    public void ProjectSnapshot_IsStrictlyProjectScoped()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Observation(ProjectA, "obs-a", 0.95));
        ledger.Record(Observation(ProjectB, "obs-b", 0.95));

        var snapshot = ledger.ProjectSnapshot(ProjectA);

        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual(ProjectA, snapshot.Single().ProjectId);
        Assert.AreEqual("obs-a", snapshot.Single().ObservationId);
    }

    [TestMethod]
    public void Promote_UsesEqualProjectWeighting()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Observation(ProjectA, "obs-a1", 0.90));
        ledger.Record(Observation(ProjectA, "obs-a2", 1.00));
        ledger.Record(Observation(ProjectB, "obs-b1", 0.85));

        var pattern = ledger.Promote("runtime.framework", FeatureHash);

        Assert.AreEqual(0.90, pattern.Confidence, 0.0001);
    }

    private static readonly Guid ProjectA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProjectC = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly string FeatureHash = new('a', 64);

    private static QirLearningObservation Observation(
        Guid projectId,
        string observationId,
        double confidence,
        QirLearningSensitivity sensitivity = QirLearningSensitivity.Public,
        QirLearningBasis basis = QirLearningBasis.Observed) =>
        new(
            observationId,
            projectId,
            "runtime.framework",
            FeatureHash,
            basis,
            sensitivity,
            confidence,
            [$"ev-{observationId}"],
            new DateTimeOffset(2026, 8, 14, 22, 30, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 14, 22, 31, 0, TimeSpan.Zero);
    }
}
