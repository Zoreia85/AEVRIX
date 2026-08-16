using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class EncryptedExecutionProofStoreTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherProject = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public async Task StoreRoundTrip_EncryptsExecutionMetadataAtRest()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var ledger = AuthorizedLedger();

        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);
        var restored = await store.LoadAsync(Project);

        Assert.IsNotNull(restored);
        Assert.AreEqual(ledger.Head, restored.Head);
        ExecutionProofLedger.VerifySnapshot(restored.Records, restored.Head);

        var file = Directory.EnumerateFiles(temp.Path, "*.aevx", SearchOption.TopDirectoryOnly).Single();
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
        Assert.IsFalse(text.Contains("exec-sensitive", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("run-sensitive", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("sandbox-worker", StringComparison.Ordinal));
        Assert.IsFalse(Path.GetFileName(file).Contains(Project.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task WrongProjectKey_CannotAuthenticateSnapshot()
    {
        using var temp = new TempDirectory();
        var good = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var ledger = AuthorizedLedger();
        await good.SaveAsync(Project, ledger.Snapshot(), ledger.Head);

        var wrong = new EncryptedExecutionProofStore(temp.Path, new FixedKeyProvider(0x7f));
        await Assert.ThrowsAsync<InvalidDataException>(() => wrong.LoadAsync(Project));
    }

    [TestMethod]
    public async Task TamperedEncryptedEnvelope_FailsClosed()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var ledger = AuthorizedLedger();
        await store.SaveAsync(Project, ledger.Snapshot(), ledger.Head);

        var path = Directory.EnumerateFiles(temp.Path, "*.aevx", SearchOption.TopDirectoryOnly).Single();
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(Project));
    }

    [TestMethod]
    public async Task PersistentLedger_RehydratesAndContinuesSameCryptographicChain()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var first = await PersistentExecutionProofLedger.OpenAsync(Project, store);
        foreach (var item in EventsThroughAuthorization()) await first.AppendAsync(item);
        var firstHead = first.Head;
        var firstEvidence = await first.BuildPromotionEvidenceAsync("exec-sensitive");

        var reopened = await PersistentExecutionProofLedger.OpenAsync(Project, store);
        Assert.AreEqual(firstHead, reopened.Head);
        var reopenedEvidence = await reopened.BuildPromotionEvidenceAsync("exec-sensitive");
        Assert.AreEqual(firstEvidence.ComputeDigestSha256(), reopenedEvidence.ComputeDigestSha256());

        await reopened.AppendAsync(Commit());
        Assert.AreEqual(firstHead.EntryCount + 1, reopened.Head.EntryCount);
        Assert.AreNotEqual(firstHead.HeadHashSha256, reopened.Head.HeadHashSha256);

        var final = await store.LoadAsync(Project);
        Assert.IsNotNull(final);
        ExecutionProofLedger.VerifySnapshot(final.Records, final.Head);
        Assert.AreEqual(reopened.Head, final.Head);
    }

    [TestMethod]
    public async Task StoreAndPersistentFacade_RejectCrossProjectMaterial()
    {
        using var temp = new TempDirectory();
        var store = new EncryptedExecutionProofStore(temp.Path, new ProjectKeyProvider());
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        ledger.Append(Started(OtherProject, "event-other-start", "exec-other"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Project, ledger.Snapshot(), ledger.Head));

        var persistent = await PersistentExecutionProofLedger.OpenAsync(Project, store);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            persistent.AppendAsync(Started(OtherProject, "event-cross", "exec-cross")));
    }

    private static ExecutionProofLedger AuthorizedLedger()
    {
        var ledger = new ExecutionProofLedger();
        foreach (var item in EventsThroughAuthorization()) ledger.Append(item);
        return ledger;
    }

    private static IReadOnlyList<ExecutionProofEvent> EventsThroughAuthorization() =>
    [
        Started(),
        Event("event-complete", ExecutionProofStage.Completed, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e'), minute: 1),
        Event("event-validation", ExecutionProofStage.ValidationCompleted, ExecutionProofOutcome.Succeeded,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), minute: 2),
        Event("event-judge", ExecutionProofStage.JudgeDecided, ExecutionProofOutcome.Approved,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), minute: 3),
        Event("event-authorize", ExecutionProofStage.PromotionAuthorized, ExecutionProofOutcome.Approved,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), promotion: H('2'), minute: 4)
    ];

    private static ExecutionProofEvent Started(
        Guid? project = null,
        string eventId = "event-start",
        string executionId = "exec-sensitive") =>
        new(eventId, project ?? Project, "run-sensitive", executionId, ExecutionProofStage.Started,
            "coding-agent", "sandbox-worker", ExecutionProofOutcome.Pending,
            H('a'), H('b'), null, null, null, null, null, null, null, At(0));

    private static ExecutionProofEvent Commit() =>
        Event("event-commit", ExecutionProofStage.PromotionCommitted, ExecutionProofOutcome.Committed,
            result: H('c'), attestation: H('d'), artifact: H('e'), validation: H('f'), judge: H('1'), promotion: H('2'),
            reference: "commit:abcdef123456", minute: 5);

    private static ExecutionProofEvent Event(
        string id,
        ExecutionProofStage stage,
        ExecutionProofOutcome outcome,
        string? result = null,
        string? attestation = null,
        string? artifact = null,
        string? validation = null,
        string? judge = null,
        string? promotion = null,
        string? reference = null,
        int minute = 0) =>
        new(id, Project, "run-sensitive", "exec-sensitive", stage, "coding-agent", "sandbox-worker", outcome,
            H('a'), H('b'), result, attestation, artifact, validation, judge, promotion, reference, At(minute));

    private static DateTimeOffset At(int minute) =>
        new DateTimeOffset(2026, 8, 15, 16, 30, 0, TimeSpan.Zero).AddMinutes(minute);

    private static string H(char value) => new(value, 64);

    private sealed class ProjectKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seed = projectId.ToByteArray();
            var key = System.Security.Cryptography.SHA256.HashData(seed);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(key);
        }
    }

    private sealed class FixedKeyProvider(byte value) : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Enumerable.Repeat(value, 32).Select(x => (byte)x).ToArray());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-execution-proof-tests", Guid.NewGuid().ToString("N"));
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
