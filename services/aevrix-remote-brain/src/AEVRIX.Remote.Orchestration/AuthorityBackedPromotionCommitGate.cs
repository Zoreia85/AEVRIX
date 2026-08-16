using System.Security.Cryptography;

namespace Aevrix.Remote.Orchestration;

public interface IExecutionPromotionAuthority : IExecutionProofHeadAnchor
{
    Task<PromotionAuthorityAttestation> RequestPromotionAttestationAsync(
        PromotionEvidenceEnvelope evidence,
        CancellationToken cancellationToken = default);
}

public sealed record AuthorityBackedPromotionCommitResult(
    PromotionEvidenceEnvelope Evidence,
    PromotionAuthorityAttestation Attestation,
    ExecutionProofRecord CommitRecord,
    string PromotionReference);

/// <summary>
/// Fail-closed boundary for irreversible promotion. The promotion callback is never invoked until
/// the local ledger proves the complete execution/validation/Judge/authorization chain, the remote
/// Authority confirms the exact same current head, and a locally verified signed attestation has
/// been issued for that exact evidence envelope. Only after the callback succeeds is
/// PromotionCommitted appended to the ledger.
/// </summary>
public sealed class AuthorityBackedPromotionCommitGate
{
    private readonly IExecutionPromotionAuthority _authority;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AuthorityBackedPromotionCommitGate(
        IExecutionPromotionAuthority authority,
        TimeProvider? timeProvider = null)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AuthorityBackedPromotionCommitResult> CommitAsync(
        ExecutionProofLedger ledger,
        string executionId,
        string commitEventId,
        Func<PromotionAuthorityAttestation, CancellationToken, Task<string>> promote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ExecutionProofEvent.ValidateSafeId(commitEventId, nameof(commitEventId), 3, 160);
        ArgumentNullException.ThrowIfNull(promote);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var evidence = ledger.BuildPromotionEvidence(executionId);
            var remoteHead = await _authority.LoadAsync(evidence.ProjectId, cancellationToken).ConfigureAwait(false);
            if (remoteHead is null || !HeadsEqual(remoteHead, evidence.LedgerHead))
                throw new InvalidOperationException(
                    "Promotion blocked because the remote Execution Authority head does not equal the local authorized ledger head.");

            var attestation = await _authority
                .RequestPromotionAttestationAsync(evidence, cancellationToken)
                .ConfigureAwait(false);

            // An IExecutionPromotionAuthority implementation owns cryptographic verification of its
            // attestation. The gate still verifies the non-secret structural binding before allowing
            // an irreversible callback, so even test/future authorities cannot silently cross-bind.
            EnsureAttestationBinding(attestation, evidence);

            var promotionReference = await promote(attestation, cancellationToken).ConfigureAwait(false);
            ExecutionProofEvent.ValidateSafeId(promotionReference, nameof(promotionReference), 3, 160);

            var authorization = FindAuthorization(ledger, evidence);
            var observedAt = _timeProvider.GetUtcNow();
            if (observedAt < authorization.ObservedAt)
                observedAt = authorization.ObservedAt;

            var committed = authorization with
            {
                EventId = commitEventId,
                Stage = ExecutionProofStage.PromotionCommitted,
                Outcome = ExecutionProofOutcome.Committed,
                PromotionReference = promotionReference,
                ObservedAt = observedAt
            };
            var record = ledger.Append(committed);
            return new AuthorityBackedPromotionCommitResult(evidence, attestation, record, promotionReference);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ExecutionProofEvent FindAuthorization(
        ExecutionProofLedger ledger,
        PromotionEvidenceEnvelope evidence)
    {
        var record = ledger.Snapshot().SingleOrDefault(item =>
            item.Event.ExecutionId == evidence.ExecutionId
            && item.Event.Stage == ExecutionProofStage.PromotionAuthorized
            && CryptographicHexEquals(item.RecordHashSha256, evidence.AuthorizationRecordHashSha256));
        return record?.Event
            ?? throw new InvalidDataException("Promotion authorization record disappeared or changed before commit.");
    }

    private static void EnsureAttestationBinding(
        PromotionAuthorityAttestation attestation,
        PromotionEvidenceEnvelope evidence)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        attestation.ValidateStructural();
        if (attestation.ProjectId != evidence.ProjectId
            || !string.Equals(attestation.RunId, evidence.RunId, StringComparison.Ordinal)
            || !string.Equals(attestation.ExecutionId, evidence.ExecutionId, StringComparison.Ordinal)
            || attestation.HeadEntryCount != evidence.LedgerHead.EntryCount
            || !CryptographicHexEquals(attestation.HeadHashSha256, evidence.LedgerHead.HeadHashSha256)
            || !CryptographicHexEquals(attestation.EvidenceDigestSha256, evidence.ComputeDigestSha256()))
        {
            throw new InvalidDataException("Promotion attestation is not bound to the locally authorized execution evidence.");
        }
    }

    private static bool HeadsEqual(ExecutionProofHead left, ExecutionProofHead right) =>
        left.EntryCount == right.EntryCount
        && CryptographicHexEquals(left.HeadHashSha256, right.HeadHashSha256);

    private static bool CryptographicHexEquals(string left, string right)
    {
        ExecutionProofEvent.ValidateSha256(left, nameof(left), required: true);
        ExecutionProofEvent.ValidateSha256(right, nameof(right), required: true);
        var a = Convert.FromHexString(left);
        var b = Convert.FromHexString(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }
}
