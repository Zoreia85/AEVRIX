using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public interface IProjectWorkspaceAwareMissionSpecialistProviderAdapter
    : IMissionSpecialistProviderAdapter
{
    SpecialistAdapterExecutionProfile ExecutionProfile { get; }

    Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        SpecialistAdapterExecutionEnvelope envelope,
        ProjectWorkspaceLease workspace,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts a workspace-aware provider into the existing governed execution-envelope contract.
/// Every attempt receives a fresh project-bound workspace lease that is destroyed after the
/// provider finishes, fails, or cooperatively cancels. The lease never broadens the provider's
/// network, filesystem, isolation, evidence, or output authority.
/// </summary>
public sealed class ProjectWorkspaceBoundAdapter
    : IExecutionEnvelopeAwareMissionSpecialistProviderAdapter
{
    private readonly IProjectWorkspaceAwareMissionSpecialistProviderAdapter _inner;
    private readonly ProjectWorkspaceLeaseManager _workspaces;

    public ProjectWorkspaceBoundAdapter(
        IProjectWorkspaceAwareMissionSpecialistProviderAdapter inner,
        ProjectWorkspaceLeaseManager workspaces)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        McpServerDescriptor.ValidateId(_inner.ProviderId, nameof(inner));
        _inner.ExecutionProfile.Validate();
    }

    public string ProviderId => _inner.ProviderId;
    public MissionSpecialistKind Kind => _inner.Kind;
    public SpecialistAdapterExecutionProfile ExecutionProfile => _inner.ExecutionProfile;

    public Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Project-bound adapters require a governed execution envelope.");

    public async Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        SpecialistAdapterExecutionEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        context.Task.Validate();
        envelope.Validate();

        if (context.Task.Specialist != Kind)
        {
            throw new InvalidDataException("Task specialist kind mismatch.");
        }

        if (envelope.WorkspaceScope == AdapterWorkspaceScope.None)
        {
            throw new InvalidOperationException(
                "Project-bound adapter execution requires a non-empty workspace scope.");
        }

        if (!ExecutionProfile.Satisfies(envelope))
        {
            throw new InvalidOperationException(
                $"Adapter '{ProviderId}' cannot satisfy the governed execution envelope.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var workId = BuildWorkId(context);
        await using var lease = _workspaces.Create(
            context.ProjectId,
            workId,
            envelope.WorkspaceScope);

        var output = await _inner.ExecuteAsync(
            context,
            envelope,
            lease,
            cancellationToken).ConfigureAwait(false);

        return output.Validate();
    }

    private string BuildWorkId(SpecialistExecutionContext context)
    {
        var candidate = $"{context.MissionId}:{context.Task.TaskId}:{ProviderId}";
        if (candidate.Length <= 120)
        {
            McpServerDescriptor.ValidateId(candidate, nameof(context));
            return candidate;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return "workspace:" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
