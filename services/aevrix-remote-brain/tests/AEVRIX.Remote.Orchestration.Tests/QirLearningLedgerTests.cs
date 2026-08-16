using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class QirLearningLedgerTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly string Hash = new('a', 64);

    [TestMethod]
    public void PromotionRequiresIndependentProjects()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Obs(A, "obs-a", .95));
        ledger.Record(Obs(A, "obs-b", .96));
        Assert.Throws<InvalidOperationException>(() => ledger.Promote("runtime.framework", Hash));
    }

    [TestMethod]
    public void PromotionProducesSanitizedAggregate()
    {
        var ledger = new QirLearningLedger(timeProvider: new FixedTimeProvider());
        ledger.Record(Obs(A, "obs-a", .95));
        ledger.Record(Obs(B, "obs-b", .91));
        var pattern = ledger.Promote("runtime.framework", Hash);
        Assert.AreEqual(2, pattern.IndependentProjectCount);
        Assert.AreEqual(2, pattern.ObservationCount);
        Assert.IsFalse(pattern.ToString().Contains(A.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(pattern.ToString().Contains(B.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(pattern.ToString().Contains("ev-", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ConfidentialAndInferredObservationsCannotSatisfyGlobalPromotion()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Obs(A, "obs-a", .95));
        ledger.Record(Obs(B, "obs-b", .95, QirLearningSensitivity.ProjectConfidential));
        ledger.Record(Obs(C, "obs-c", .95, basis: QirLearningBasis.Inferred));
        Assert.Throws<InvalidOperationException>(() => ledger.Promote("runtime.framework", Hash));
    }

    [TestMethod]
    public void SecretsAndPublicPersonalDataAreRejected()
    {
        var ledger = new QirLearningLedger();
        Assert.Throws<InvalidDataException>(() => ledger.Record(Obs(A, "obs-secret", .95) with { ContainsRawSecretMaterial = true }));
        Assert.Throws<InvalidDataException>(() => ledger.Record(Obs(A, "obs-pii", .95) with { ContainsPersonalData = true }));
    }

    [TestMethod]
    public void ObservationIdsAreImmutableWithinProject()
    {
        var ledger = new QirLearningLedger();
        var first = Obs(A, "obs-fixed", .95);
        Assert.AreSame(ledger.Record(first), ledger.Record(first));
        Assert.Throws<InvalidOperationException>(() => ledger.Record(first with { Confidence = .70 }));
    }

    [TestMethod]
    public void ProjectSnapshotsDoNotLeakAcrossProjects()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Obs(A, "obs-a", .95));
        ledger.Record(Obs(B, "obs-b", .95));
        var snapshot = ledger.ProjectSnapshot(A);
        Assert.AreEqual(1, snapshot.Count);
        Assert.AreEqual(A, snapshot.Single().ProjectId);
    }

    [TestMethod]
    public void PromotionUsesEqualProjectWeighting()
    {
        var ledger = new QirLearningLedger();
        ledger.Record(Obs(A, "obs-a1", .90));
        ledger.Record(Obs(A, "obs-a2", 1.00));
        ledger.Record(Obs(B, "obs-b1", .85));
        Assert.AreEqual(.90, ledger.Promote("runtime.framework", Hash).Confidence, .0001);
    }

    private static QirLearningObservation Obs(
        Guid projectId, string id, double confidence,
        QirLearningSensitivity sensitivity = QirLearningSensitivity.Public,
        QirLearningBasis basis = QirLearningBasis.Observed) =>
        new(id, projectId, "runtime.framework", Hash, basis, sensitivity, confidence,
            [$"ev-{id}"], new DateTimeOffset(2026, 8, 14, 22, 30, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 14, 22, 31, 0, TimeSpan.Zero);
    }
}
