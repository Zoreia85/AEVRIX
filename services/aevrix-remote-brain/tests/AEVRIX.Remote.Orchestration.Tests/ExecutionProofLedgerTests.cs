using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ExecutionProofLedgerTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public void HappyPath_BuildsJudgeBoundPromotionEvidence()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        ledger.Append(Completed());
        ledger.Append(Validation());
        ledger.Append(Judge());
        var authorization = ledger.Append(Authorization());

        var evidence = ledger.BuildPromotionEvidence("exec-001");
        ExecutionProofLedger.VerifySnapshot(ledger.Snapshot(), ledger.Head);

        Assert.AreEqual(5, evidence.LedgerHead.EntryCount);
        Assert.AreEqual(ArtifactHash, evidence.ArtifactManifestSha256);
        Assert.AreEqual(ValidationHash, evidence.ValidationDigestSha256);
        Assert.AreEqual(JudgeHash, evidence.JudgeDecisionDigestSha256);
        Assert.AreEqual(PromotionHash, evidence.PromotionDigestSha256);
        Assert.AreEqual(authorization.RecordHashSha256, evidence.AuthorizationRecordHashSha256);
        Assert.AreEqual(64, evidence.ComputeDigestSha256().Length);
    }

    [TestMethod]
    public void VerifySnapshot_RejectsPayloadTampering()
    {
        var ledger = AuthorizedLedger();
        var snapshot = ledger.Snapshot().ToArray();
        snapshot[1] = snapshot[1] with
        {
            Event = snapshot[1].Event with { ResultDigestSha256 = Hash('9') }
        };

        Assert.Throws<InvalidDataException>(() =>
            ExecutionProofLedger.VerifySnapshot(snapshot, ledger.Head));
    }

    [TestMethod]
    public void VerifySnapshot_RejectsBrokenPreviousHash()
    {
        var ledger = AuthorizedLedger();
        var snapshot = ledger.Snapshot().ToArray();
        snapshot[2] = snapshot[2] with { PreviousRecordHashSha256 = Hash('8') };

        Assert.Throws<InvalidDataException>(() =>
            ExecutionProofLedger.VerifySnapshot(snapshot, ledger.Head));
    }

    [TestMethod]
    public void VerifySnapshot_RejectsReordering()
    {
        var ledger = AuthorizedLedger();
        var original = ledger.Snapshot();
        var reordered = new[] { original[0], original[2], original[1], original[3], original[4] };

        Assert.Throws<InvalidDataException>(() =>
            ExecutionProofLedger.VerifySnapshot(reordered, ledger.Head));
    }

    [TestMethod]
    public void VerifySnapshot_ExternallyRetainedHeadDetectsTailTruncation()
    {
        var ledger = AuthorizedLedger();
        var expectedHead = ledger.Head;
        var truncated = ledger.Snapshot().Take(4).ToArray();

        Assert.Throws<InvalidDataException>(() =>
            ExecutionProofLedger.VerifySnapshot(truncated, expectedHead));
    }

    [TestMethod]
    public void Append_RejectsCrossRunReplayForSameExecutionId()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        var replay = Completed() with { RunId = "other-run" };

        Assert.Throws<InvalidDataException>(() => ledger.Append(replay));
        Assert.AreEqual(1, ledger.Head.EntryCount);
        ExecutionProofLedger.VerifySnapshot(ledger.Snapshot(), ledger.Head);
    }

    [TestMethod]
    public void Append_RejectsEventIdReuse()
    {
        var ledger = new ExecutionProofLedger();
        var started = Started();
        ledger.Append(started);

        Assert.Throws<InvalidOperationException>(() => ledger.Append(started));
        Assert.AreEqual(1, ledger.Head.EntryCount);
    }

    [TestMethod]
    public void Validation_RequiresSuccessfulArtifactBearingExecution()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        ledger.Append(Completed() with
        {
            Outcome = ExecutionProofOutcome.Succeeded,
            ArtifactManifestSha256 = null
        });

        Assert.Throws<InvalidDataException>(() => ledger.Append(Validation()));
        Assert.AreEqual(2, ledger.Head.EntryCount);
    }

    [TestMethod]
    public void PromotionAuthorization_RequiresApprovedJudgeDecision()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        ledger.Append(Completed());
        ledger.Append(Validation());
        ledger.Append(Judge() with { Outcome = ExecutionProofOutcome.Rejected });

        Assert.Throws<InvalidDataException>(() => ledger.Append(Authorization()));
        Assert.Throws<InvalidOperationException>(() => ledger.BuildPromotionEvidence("exec-001"));
    }

    [TestMethod]
    public void PromotionCommit_MustRemainBoundToAuthorizedDigest()
    {
        var ledger = AuthorizedLedger();
        var forged = Commit() with { PromotionDigestSha256 = Hash('7') };

        Assert.Throws<InvalidDataException>(() => ledger.Append(forged));
        var committed = ledger.Append(Commit());

        Assert.AreEqual(ExecutionProofStage.PromotionCommitted, committed.Event.Stage);
        Assert.AreEqual("commit:abcdef123456", committed.Event.PromotionReference);
        ExecutionProofLedger.VerifySnapshot(ledger.Snapshot(), ledger.Head);
    }

    [TestMethod]
    public void Contract_IsContentMinimizingAndContainsNoRawPayloadFields()
    {
        var names = typeof(ExecutionProofEvent).GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.IsFalse(names.Any(name => name.Contains("Prompt", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(names.Any(name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(names.Any(name => name.Contains("Raw", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(names.Any(name => name is "Output" or "Content" or "ArtifactContent"));
    }

    private static ExecutionProofLedger AuthorizedLedger()
    {
        var ledger = new ExecutionProofLedger();
        ledger.Append(Started());
        ledger.Append(Completed());
        ledger.Append(Validation());
        ledger.Append(Judge());
        ledger.Append(Authorization());
        return ledger;
    }

    private static ExecutionProofEvent Started() => new(
        "event-start", Project, "run-001", "exec-001", ExecutionProofStage.Started,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Pending,
        InputHash, AuthorityHash, null, null, null, null, null, null, null, At(0));

    private static ExecutionProofEvent Completed() => new(
        "event-complete", Project, "run-001", "exec-001", ExecutionProofStage.Completed,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Succeeded,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, null, null, null, null, At(1));

    private static ExecutionProofEvent Validation() => new(
        "event-validation", Project, "run-001", "exec-001", ExecutionProofStage.ValidationCompleted,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Succeeded,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, null, null, null, At(2));

    private static ExecutionProofEvent Judge() => new(
        "event-judge", Project, "run-001", "exec-001", ExecutionProofStage.JudgeDecided,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Approved,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, JudgeHash, null, null, At(3));

    private static ExecutionProofEvent Authorization() => new(
        "event-authorize", Project, "run-001", "exec-001", ExecutionProofStage.PromotionAuthorized,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Approved,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, JudgeHash, PromotionHash, null, At(4));

    private static ExecutionProofEvent Commit() => new(
        "event-commit", Project, "run-001", "exec-001", ExecutionProofStage.PromotionCommitted,
        "coding-agent", "sandbox-worker", ExecutionProofOutcome.Committed,
        InputHash, AuthorityHash, ResultHash, AttestationHash, ArtifactHash, ValidationHash, JudgeHash, PromotionHash,
        "commit:abcdef123456", At(5));

    private static DateTimeOffset At(int minutes) =>
        new DateTimeOffset(2026, 8, 15, 16, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private static string Hash(char value) => new(value, 64);
    private static string InputHash => Hash('a');
    private static string AuthorityHash => Hash('b');
    private static string ResultHash => Hash('c');
    private static string AttestationHash => Hash('d');
    private static string ArtifactHash => Hash('e');
    private static string ValidationHash => Hash('f');
    private static string JudgeHash => Hash('1');
    private static string PromotionHash => Hash('2');
}
