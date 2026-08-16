using System.Collections.Concurrent;

namespace Aevrix.Remote.Orchestration;

public sealed record AttestedPromotionRequest(
    PromotionAuthorityAttestation Attestation,
    PromotionEvidenceEnvelope Evidence);

public sealed record AttestedPromotionReceipt(
    VerifiedPromotionAuthorityAttestation Authority,
    string ReplayKey);

public interface IAttestedPromotionSink
{
    Task PromoteAsync(
        AttestedPromotionReceipt receipt,
        PromotionEvidenceEnvelope evidence,
        CancellationToken cancellationToken = default);
}

public interface IPromotionReplayGuard
{
    bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey);
}

/// <summary>
/// Process-local at-most-once guard keyed to the exact promotion identity rather than to the
/// Authority nonce. Re-signing identical evidence therefore cannot bypass replay protection.
/// Durable or distributed deployments should replace this adapter with a transactional store.
/// </summary>
public sealed class InMemoryPromotionReplayGuard : IPromotionReplayGuard
{
    private readonly ConcurrentDictionary<string, byte> _claims = new(StringComparer.Ordinal);

    public bool TryClaim(VerifiedPromotionAuthorityAttestation attestation, out string replayKey)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        replayKey = BuildReplayKey(attestation);
        return _claims.TryAdd(replayKey, 0);
    }

    internal static string BuildReplayKey(VerifiedPromotionAuthorityAttestation attestation) =>
        string.Join(':', new[]
        {
            attestation.ProjectId.ToString("D"),
            attestation.RunId,
            attestation.ExecutionId,
            attestation.EvidenceDigestSha256.ToLowerInvariant(),
            attestation.LedgerHead.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            attestation.LedgerHead.HeadHashSha256.ToLowerInvariant()
        });
}

/// <summary>
/// Promotion-side trust gate. No sink is called until the independent Authority signature,
/// evidence binding, key pinning, validity window and anchored-ledger-head requirements have all
/// been verified. A successful claim is deliberately not released after sink failure: promotion
/// side effects may be ambiguous, so automatic retry would weaken at-most-once semantics.
/// </summary>
public sealed class AttestedPromotionGate
{
    private readonly PromotionAuthorityAttestationVerifier _verifier;
    private readonly IPromotionReplayGuard _replayGuard;
    private readonly IAttestedPromotionSink _sink;

    public AttestedPromotionGate(
        PromotionAuthorityAttestationVerifier verifier,
        IPromotionReplayGuard replayGuard,
        IAttestedPromotionSink sink)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _replayGuard = replayGuard ?? throw new ArgumentNullException(nameof(replayGuard));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public async Task<AttestedPromotionReceipt> PromoteAsync(
        AttestedPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var verified = _verifier.Verify(request.Attestation, request.Evidence);
        if (!_replayGuard.TryClaim(verified, out var replayKey))
        {
            throw new InvalidOperationException(
                "Promotion replay rejected: this project/run/execution/evidence/head identity was already claimed.");
        }

        var receipt = new AttestedPromotionReceipt(verified, replayKey);
        await _sink.PromoteAsync(receipt, request.Evidence, cancellationToken).ConfigureAwait(false);
        return receipt;
    }
}
