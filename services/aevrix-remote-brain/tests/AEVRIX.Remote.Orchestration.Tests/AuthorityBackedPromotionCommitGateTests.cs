using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class AuthorityBackedPromotionCommitGateTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 20, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CommitAsync_ValidRemoteHeadAndAttestation_AllowsCallbackThenAppendsCommit()
    {
        var ledger = AuthorizedLedger();
        var authority = new FakeAuthority(ledger.Head);
        var gate = new AuthorityBackedPromotionCommitGate(authority, new FixedTimeProvider(Now));
        var callbackInvoked = false;

        var result = await gate.CommitAsync(
            ledger,
            "exec-001",
            "event-remote-commit",
            (attestation, _) =>
            {
                callbackInvoked = true;
                Assert.AreEqual(ledger.Head.EntryCount - 1, attestation.HeadEntryCount);
                return Task.FromResult("commit:authority123");
            });

        Assert.IsTrue(callbackInvoked);
        Assert.AreEqual(ExecutionProofStage.PromotionCommitted, result.CommitRecord.Event.Stage);
        Assert.AreEqual("commit:authority123", result.PromotionReference);
        Assert.AreEqual(6, ledger.Head.EntryCount);
        ExecutionProofLedger.VerifySnapshot(ledger.Snapshot(), ledger.Head);
    }

    [TestMethod]
    public async Task CommitAsync_RemoteHeadMismatch_BlocksBeforeIrreversibleCallback()
    {
        var ledger = AuthorizedLedger();
        var authority = new FakeAuthority(new ExecutionProofHead(ledger.Head.EntryCount, H('9')));
        var gate = new AuthorityBackedPromotionCommitGate(authority);
        var callbackInvoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.CommitAsync(
            ledger,
            "exec-001",
            "event-remote-commit",
            (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult("commit:should-not-run");
            }));

        Assert.IsFalse(callbackInvoked);
        Assert.AreEqual(5, ledger.Head.EntryCount);
    }

    [TestMethod]
    public async Task CommitAsync_AuthorityUnavailable_BlocksBeforeIrreversibleCallback()
    {
        var ledger = AuthorizedLedger();
        var gate = new AuthorityBackedPromotionCommitGate(new FakeAuthority(null));
        var callbackInvoked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.CommitAsync(
            ledger,
            "exec-001",
            "event-remote-commit",
            (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult("commit:should-not-run");
            }));

        Assert.IsFalse(callbackInvoked);
    }

    [TestMethod]
    public async Task CommitAsync_CrossBoundAttestation_BlocksBeforeIrreversibleCallback()
    {
        var ledger = AuthorizedLedger();
        var authority = new FakeAuthority(ledger.Head) { TamperAttestationHead = true };
        var gate = new AuthorityBackedPromotionCommitGate(authority);
        var callbackInvoked = false;

        await Assert.ThrowsAsync<InvalidDataException>(() => gate.CommitAsync(
            ledger,
            "exec-001",
            "event-remote-commit",
            (_, _) =>
            {
                callbackInvoked = true;
                return Task.FromResult("commit:should-not-run");
            }));

        Assert.IsFalse(callbackInvoked);
        Assert.AreEqual(5, ledger.Head.EntryCount);
    }

    [TestMethod]
    public async Task CommitAsync_CallbackFailure_DoesNotClaimPromotionCommitted()
    {
        var ledger = AuthorizedLedger();
        var gate = new AuthorityBackedPromotionCommitGate(new FakeAuthority(ledger.Head));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.CommitAsync(
            ledger,
            "exec-001",
            "event-remote-commit",
            (_, _) => throw new InvalidOperationException("synthetic promotion failure")));

        Assert.AreEqual(5, ledger.Head.EntryCount);
        Assert.IsFalse(ledger.Snapshot().Any(record => record.Event.Stage == ExecutionProofStage.PromotionCommitted));
    }

    private static ExecutionProofLedger AuthorizedLedger()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Event("start", ExecutionProofStage.Started, ExecutionProofOutcome.Pending, null, null, null, null, null));
        ledger.Append(Event("complete", ExecutionProofStage.Completed, ExecutionProofOutcome.Succeeded, H('c'), H('e'), null, null, null));
        ledger.Append(Event("validation", ExecutionProofStage.ValidationCompleted, ExecutionProofOutcome.Succeeded, H('c'), H('e'), H('f'), null, null));
        ledger.Append(Event("judge", ExecutionProofStage.JudgeDecided, ExecutionProofOutcome.Approved, H('c'), H('e'), H('f'), H('1'), null));
        ledger.Append(Event("authorize", ExecutionProofStage.PromotionAuthorized, ExecutionProofOutcome.Approved, H('c'), H('e'), H('f'), H('1'), H('2')));
        return ledger;
    }

    private static ExecutionProofEvent Event(
        string id,
        ExecutionProofStage stage,
        ExecutionProofOutcome outcome,
        string? result,
        string? artifact,
        string? validation,
        string? judge,
        string? promotion) => new(
            "event-" + id,
            Project,
            "run-001",
            "exec-001",
            stage,
            "coding-agent",
            "sandbox-worker",
            outcome,
            H('a'),
            H('b'),
            result,
            result is null ? null : H('d'),
            artifact,
            validation,
            judge,
            promotion,
            null,
            Now.AddMinutes((int)stage));

    private static string H(char value) => new(value, 64);

    private sealed class FakeAuthority(ExecutionProofHead? head) : IExecutionPromotionAuthority
    {
        public bool TamperAttestationHead { get; init; }

        public Task<ExecutionProofHead?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(head);

        public Task AdvanceAsync(
            Guid projectId,
            ExecutionProofHead expectedPrevious,
            ExecutionProofHead next,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PromotionAuthorityAttestation> RequestPromotionAttestationAsync(
            PromotionEvidenceEnvelope evidence,
            CancellationToken cancellationToken = default)
        {
            var attestedHead = TamperAttestationHead ? H('9') : evidence.LedgerHead.HeadHashSha256;
            return Task.FromResult(new PromotionAuthorityAttestation(
                PromotionAuthorityAttestation.CurrentVersion,
                "authority-test-key",
                evidence.ProjectId,
                evidence.RunId,
                evidence.ExecutionId,
                evidence.ComputeDigestSha256(),
                evidence.LedgerHead.EntryCount,
                attestedHead,
                Now.AddSeconds(-5).ToUnixTimeSeconds(),
                Now.AddMinutes(5).ToUnixTimeSeconds(),
                "0123456789abcdef0123456789abcdef",
                "AA==",
                H('8')));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
