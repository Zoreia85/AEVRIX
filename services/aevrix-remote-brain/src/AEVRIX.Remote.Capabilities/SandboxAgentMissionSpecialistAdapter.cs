using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public sealed record SandboxAgentMissionSpecialistAdapterOptions(
    TimeSpan PollInterval,
    int MaximumPollCount = 2_000)
{
    public SandboxAgentMissionSpecialistAdapterOptions Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(10)
            || PollInterval > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (MaximumPollCount is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPollCount));
        }

        return this;
    }
}

/// <summary>
/// Adapts an approved AEVRIX sandbox-agent backend into the mission specialist fabric.
/// The backend remains out-of-process and must attest its approved isolation boundary;
/// this adapter never grants host filesystem access, broadens evidence authority, or
/// treats backend output as trusted knowledge.
/// </summary>
public sealed class SandboxAgentMissionSpecialistAdapter
    : IProjectWorkspaceAwareMissionSpecialistProviderAdapter
{
    private readonly SandboxAgentBackendClient _client;
    private readonly SandboxAgentMissionSpecialistAdapterOptions _options;

    public SandboxAgentMissionSpecialistAdapter(
        MissionSpecialistKind kind,
        SandboxAgentBackendClient client,
        SpecialistAdapterExecutionProfile executionProfile,
        SandboxAgentMissionSpecialistAdapterOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ExecutionProfile = (executionProfile
            ?? throw new ArgumentNullException(nameof(executionProfile))).Validate();
        _options = (options ?? new SandboxAgentMissionSpecialistAdapterOptions(
            TimeSpan.FromMilliseconds(250))).Validate();

        if (ExecutionProfile.IsolationLevel is not (AgentIsolationLevel.Container or AgentIsolationLevel.VirtualMachine))
        {
            throw new ArgumentException(
                "Sandbox-agent specialist adapters require container or virtual-machine isolation.",
                nameof(executionProfile));
        }

        Kind = kind;
        McpServerDescriptor.ValidateId(_client.BackendId, nameof(client));
    }

    public string ProviderId => _client.BackendId;
    public MissionSpecialistKind Kind { get; }
    public SpecialistAdapterExecutionProfile ExecutionProfile { get; }

    public Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Sandbox-agent specialist execution requires a governed envelope and project workspace lease.");

    public async Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        SpecialistAdapterExecutionEnvelope envelope,
        ProjectWorkspaceLease workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(workspace);
        context.Task.Validate();
        envelope.Validate();

        if (context.Task.Specialist != Kind)
        {
            throw new InvalidDataException("Task specialist kind mismatch.");
        }

        if (workspace.IsDisposed
            || workspace.ProjectId != context.ProjectId
            || workspace.WorkspaceScope != envelope.WorkspaceScope)
        {
            throw new InvalidDataException("Project workspace lease does not match the governed specialist execution context.");
        }

        if (!ExecutionProfile.Satisfies(envelope))
        {
            throw new InvalidOperationException(
                $"Sandbox backend '{ProviderId}' cannot satisfy the governed execution envelope.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var request = new AgentWorkRequest(
            BuildWorkId(context),
            context.Task.Objective,
            workspace.RootPath,
            context.Task.EvidenceIds);

        var receipt = await _client.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        AgentJobResult? result = null;

        for (var poll = 0; poll < _options.MaximumPollCount; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await _client.GetResultAsync(
                receipt.JobId,
                workspace.RootPath,
                cancellationToken).ConfigureAwait(false);

            if (result.State is AgentJobState.Queued or AgentJobState.Running)
            {
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            break;
        }

        if (result is null || result.State is AgentJobState.Queued or AgentJobState.Running)
        {
            throw new TimeoutException(
                $"Sandbox backend '{ProviderId}' did not reach a terminal state within the bounded poll budget.");
        }

        if (result.State != AgentJobState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Sandbox backend '{ProviderId}' finished with state '{result.State}'.");
        }

        var unknownEvidence = result.EvidenceIds
            .Except(context.Task.EvidenceIds, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownEvidence.Length > 0)
        {
            throw new InvalidDataException(
                "Sandbox backend returned evidence outside the governed specialist boundary.");
        }

        var artifacts = BuildArtifactIds(result);
        var summary = string.IsNullOrWhiteSpace(result.OutputSummary)
            ? $"Sandbox backend '{ProviderId}' completed the authorized specialist task."
            : result.OutputSummary.Trim();

        var output = new SpecialistExecutionOutput(
            summary,
            Confidence: 0.90,
            result.EvidenceIds,
            artifacts);
        envelope.ValidateOutput(output);
        return output.Validate();
    }

    private string BuildWorkId(SpecialistExecutionContext context)
    {
        var candidate = $"agent:{context.MissionId}:{context.Task.TaskId}:{ProviderId}";
        if (candidate.Length <= 120)
        {
            McpServerDescriptor.ValidateId(candidate, nameof(context));
            return candidate;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return "agent:" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static IReadOnlyList<string> BuildArtifactIds(AgentJobResult result)
    {
        if (result.ArtifactManifestSha256 is null)
        {
            return Array.Empty<string>();
        }

        return ["manifest:" + result.ArtifactManifestSha256.ToLowerInvariant()];
    }
}
