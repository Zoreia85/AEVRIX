using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public enum AdapterNetworkScope
{
    None,
    LoopbackOnly,
    Allowlisted
}

public enum AdapterWorkspaceScope
{
    None,
    ReadOnly,
    ReadWrite
}

public sealed record SpecialistAdapterExecutionEnvelope(
    AdapterNetworkScope NetworkScope,
    AdapterWorkspaceScope WorkspaceScope,
    AgentIsolationLevel MinimumIsolation,
    int MaximumSummaryUtf8Bytes = 256_000,
    int MaximumEvidenceIds = 2_000,
    int MaximumArtifactIds = 2_000)
{
    public static SpecialistAdapterExecutionEnvelope RestrictiveDefault { get; } =
        new(
            AdapterNetworkScope.None,
            AdapterWorkspaceScope.ReadOnly,
            AgentIsolationLevel.Container);

    public SpecialistAdapterExecutionEnvelope Validate()
    {
        if (!Enum.IsDefined(NetworkScope)
            || !Enum.IsDefined(WorkspaceScope)
            || !Enum.IsDefined(MinimumIsolation))
        {
            throw new ArgumentOutOfRangeException(nameof(NetworkScope));
        }

        if (MaximumSummaryUtf8Bytes is < 1_024 or > 16_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSummaryUtf8Bytes));
        }

        if (MaximumEvidenceIds is < 1 or > 2_000
            || MaximumArtifactIds is < 0 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEvidenceIds));
        }

        return this;
    }

    public void ValidateOutput(SpecialistExecutionOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Validate();

        if (Encoding.UTF8.GetByteCount(output.Summary) > MaximumSummaryUtf8Bytes)
        {
            throw new InvalidDataException("Adapter output summary exceeds the governed UTF-8 byte budget.");
        }

        if (output.EvidenceIds.Count > MaximumEvidenceIds)
        {
            throw new InvalidDataException("Adapter output exceeds the governed evidence-id budget.");
        }

        if (output.ArtifactIds.Count > MaximumArtifactIds)
        {
            throw new InvalidDataException("Adapter output exceeds the governed artifact-id budget.");
        }
    }
}

public sealed record SpecialistAdapterExecutionProfile(
    AdapterNetworkScope MaximumNetworkScope,
    AdapterWorkspaceScope MaximumWorkspaceScope,
    AgentIsolationLevel IsolationLevel)
{
    public SpecialistAdapterExecutionProfile Validate()
    {
        if (!Enum.IsDefined(MaximumNetworkScope)
            || !Enum.IsDefined(MaximumWorkspaceScope)
            || !Enum.IsDefined(IsolationLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumNetworkScope));
        }

        return this;
    }

    public bool Satisfies(SpecialistAdapterExecutionEnvelope envelope)
    {
        envelope.Validate();
        Validate();

        return MaximumNetworkScope >= envelope.NetworkScope
            && MaximumWorkspaceScope >= envelope.WorkspaceScope
            && IsolationStrength(IsolationLevel) >= IsolationStrength(envelope.MinimumIsolation);
    }

    private static int IsolationStrength(AgentIsolationLevel isolation) => isolation switch
    {
        AgentIsolationLevel.LocalProcess => 0,
        AgentIsolationLevel.Container => 1,
        AgentIsolationLevel.VirtualMachine => 2,
        _ => -1
    };
}

public interface IExecutionEnvelopeAwareMissionSpecialistProviderAdapter
    : IMissionSpecialistProviderAdapter
{
    SpecialistAdapterExecutionProfile ExecutionProfile { get; }

    Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        SpecialistAdapterExecutionEnvelope envelope,
        CancellationToken cancellationToken = default);
}
