using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

/// <summary>
/// Resolves the opaque security subject that owns a workspace execution.
/// Implementations may bind this to an authenticated principal, local OS identity,
/// tenant subject, or another reviewed identity source. The value is never used as
/// a plaintext filesystem path component.
/// </summary>
public interface IWorkspaceSubjectResolver
{
    string ResolveSubjectId(SpecialistExecutionContext context);
}

public sealed class FixedWorkspaceSubjectResolver(string subjectId) : IWorkspaceSubjectResolver
{
    public string ResolveSubjectId(SpecialistExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        McpServerDescriptor.ValidateId(subjectId, nameof(subjectId));
        return subjectId;
    }
}

/// <summary>
/// Workspace-aware adapter that creates an ephemeral namespace bound to both an
/// opaque subject and project. This is a logical containment boundary: it prevents
/// accidental cross-subject namespace reuse but does not claim OS ACL/token isolation.
/// </summary>
public sealed class SubjectProjectWorkspaceBoundAdapter
    : IExecutionEnvelopeAwareMissionSpecialistProviderAdapter
{
    private readonly IProjectWorkspaceAwareMissionSpecialistProviderAdapter _inner;
    private readonly ProjectWorkspaceLeaseManager _workspaces;
    private readonly IWorkspaceSubjectResolver _subjects;

    public SubjectProjectWorkspaceBoundAdapter(
        IProjectWorkspaceAwareMissionSpecialistProviderAdapter inner,
        ProjectWorkspaceLeaseManager workspaces,
        IWorkspaceSubjectResolver subjects)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
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
            "Subject/project-bound adapters require a governed execution envelope.");

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
                "Subject/project-bound adapter execution requires a non-empty workspace scope.");
        }
        if (!ExecutionProfile.Satisfies(envelope))
        {
            throw new InvalidOperationException(
                $"Adapter '{ProviderId}' cannot satisfy the governed execution envelope.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var subjectId = _subjects.ResolveSubjectId(context);
        McpServerDescriptor.ValidateId(subjectId, nameof(subjectId));
        var workId = BuildWorkId(context);

        await using var lease = _workspaces.Create(
            context.ProjectId,
            subjectId,
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
