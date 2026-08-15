using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class PromotionRecoveryJournalTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset PreparedAt = new(2026, 8, 15, 22, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void OperationId_IsKeyedAndStableForSameEvidence()
    {
        using var tempA = new TempDirectory();
        using var tempB = new TempDirectory();
        using var first = Journal(tempA.Path, 0x11, 0x21);
        using var sameKey = Journal(tempB.Path, 0x11, 0x31);
        using var otherKey = Journal(tempB.Path + "-other", 0x12, 0x32);
        var evidence = Evidence();

        var firstId = first.ComputeOperationId(evidence);
        var sameId = sameKey.ComputeOperationId(evidence);
        var otherId = otherKey.ComputeOperationId(evidence);

        Assert.AreEqual(firstId, sameId);
        Assert.AreNotEqual(firstId, otherId);
        Assert.AreEqual(64, firstId.Length);
        Assert.IsTrue(firstId.All(Uri.IsHexDigit));
    }

    [TestMethod]
    public async Task PreparedAppliedCommitted_TransitionsAreDurableAndIdempotent()
    {
        using var temp = new TempDirectory();
        using var journal = Journal(temp.Path, 0x11, 0x21);
        var evidence = Evidence();
        var prepared = await journal.PrepareAsync(evidence, "event-durable-commit", PreparedAt);
        var preparedAgain = await journal.PrepareAsync(evidence, "event-durable-commit", PreparedAt.AddHours(1));

        Assert.AreEqual(PromotionRecoveryState.Prepared, prepared.State);
        Assert.AreEqual(prepared, preparedAgain);
        Assert.AreEqual(1, Directory.EnumerateFiles(temp.Path, "*.journal").Count());

        var receipt = new PromotionExecutionReceipt(prepared.OperationId, "commit:external-001", PreparedAt.AddMinutes(1));
        var applied = await journal.MarkAppliedAsync(prepared.OperationId, receipt);
        var appliedAgain = await journal.MarkAppliedAsync(prepared.OperationId, receipt);
        Assert.AreEqual(applied, appliedAgain);
        Assert.AreEqual(PromotionRecoveryState.Applied, applied.State);

        var committed = await journal.MarkLedgerCommittedAsync(prepared.OperationId, H('9'), PreparedAt.AddMinutes(2));
        var committedAgain = await journal.MarkLedgerCommittedAsync(prepared.OperationId, H('9'), PreparedAt.AddMinutes(3));
        Assert.AreEqual(committed, committedAgain);
        Assert.AreEqual(PromotionRecoveryState.LedgerCommitted, committed.State);
        Assert.AreEqual(H('9'), committed.CommitRecordHashSha256);

        var loaded = await journal.LoadAsync(prepared.OperationId);
        Assert.AreEqual(committed, loaded);
    }

    [TestMethod]
    public async Task TamperingJournalBytes_IsRejectedByIntegrityMac()
    {
        using var temp = new TempDirectory();
        using var journal = Journal(temp.Path, 0x11, 0x21);
        var prepared = await journal.PrepareAsync(Evidence(), "event-durable-commit", PreparedAt);
        var path = Directory.EnumerateFiles(temp.Path, "*.journal").Single();
        var text = await File.ReadAllTextAsync(path);
        text = text.Replace("event-durable-commit", "event-forged-commit", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text);

        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => journal.LoadAsync(prepared.OperationId));
    }

    [TestMethod]
    public async Task IllegalOrCrossBoundTransitions_AreRejected()
    {
        using var temp = new TempDirectory();
        using var journal = Journal(temp.Path, 0x11, 0x21);
        var prepared = await journal.PrepareAsync(Evidence(), "event-durable-commit", PreparedAt);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.MarkLedgerCommittedAsync(prepared.OperationId, H('9'), PreparedAt.AddMinutes(1)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.MarkAppliedAsync(prepared.OperationId,
                new PromotionExecutionReceipt(H('f'), "commit:wrong-operation", PreparedAt.AddMinutes(1))));

        var otherEvidence = Evidence() with { PromotionDigestSha256 = H('7') };
        var otherOperation = journal.ComputeOperationId(otherEvidence);
        Assert.AreNotEqual(prepared.OperationId, otherOperation);
    }

    [TestMethod]
    public async Task SameOperationCannotBeReboundToDifferentCommitEvent()
    {
        using var temp = new TempDirectory();
        using var journal = Journal(temp.Path, 0x11, 0x21);
        var evidence = Evidence();
        await journal.PrepareAsync(evidence, "event-durable-commit-a", PreparedAt);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            journal.PrepareAsync(evidence, "event-durable-commit-b", PreparedAt));
    }

    private static FileBackedPromotionRecoveryJournal Journal(string path, byte operation, byte integrity)
    {
        Directory.CreateDirectory(path);
        return new FileBackedPromotionRecoveryJournal(path,
            Enumerable.Repeat(operation, 32).Select(value => (byte)value).ToArray(),
            Enumerable.Repeat(integrity, 32).Select(value => (byte)value).ToArray());
    }

    private static PromotionEvidenceEnvelope Evidence() => new(
        1,
        Project,
        "run-durable",
        "exec-durable",
        "coding-agent",
        "sandbox-worker",
        H('a'), H('b'), H('c'), H('d'), H('e'),
        new ExecutionProofHead(5, H('f')));

    private static string H(char value) => new(value, 64);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-promotion-journal", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
