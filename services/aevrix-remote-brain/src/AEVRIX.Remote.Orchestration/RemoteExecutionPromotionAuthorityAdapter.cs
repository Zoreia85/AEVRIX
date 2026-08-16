namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Keeps the transport client focused while exposing its already-verified operations through the
/// narrow authority interface consumed by the irreversible promotion gate.
/// </summary>
public sealed class RemoteExecutionPromotionAuthorityAdapter : IExecutionPromotionAuthority
{
    private readonly RemoteExecutionAuthorityClient _client;

    public RemoteExecutionPromotionAuthorityAdapter(RemoteExecutionAuthorityClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<ExecutionProofHead?> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        _client.LoadAsync(projectId, cancellationToken);

    public Task AdvanceAsync(
        Guid projectId,
        ExecutionProofHead expectedPrevious,
        ExecutionProofHead next,
        CancellationToken cancellationToken = default) =>
        _client.AdvanceAsync(projectId, expectedPrevious, next, cancellationToken);

    public Task<PromotionAuthorityAttestation> RequestPromotionAttestationAsync(
        PromotionEvidenceEnvelope evidence,
        CancellationToken cancellationToken = default) =>
        _client.RequestPromotionAttestationAsync(evidence, cancellationToken);
}
