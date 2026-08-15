namespace Aevrix.Remote.Capabilities;

public enum OutOfProcessFilesystemScope
{
    Unrestricted,
    WorkspaceOnly,
    WorkspaceReadOnly
}

public sealed record OutOfProcessFilesystemPolicy(OutOfProcessFilesystemScope Scope)
{
    public OutOfProcessFilesystemPolicy Validate()
    {
        if (!Enum.IsDefined(Scope))
        {
            throw new ArgumentOutOfRangeException(nameof(Scope));
        }

        return this;
    }

    public bool RequiresIsolation => Scope != OutOfProcessFilesystemScope.Unrestricted;
}

/// <summary>
/// Fail-closed filesystem authority gate for the pinned process runtime.
/// Working-directory containment does not prevent a same-token child process from opening
/// arbitrary host paths. Until an AppContainer/restricted-token/container backend proves
/// filesystem isolation, WorkspaceOnly and WorkspaceReadOnly scopes are rejected before launch.
/// </summary>
public sealed class FilesystemGovernedOutOfProcessRuntime
{
    private readonly PinnedOutOfProcessRuntime _runtime;
    private readonly OutOfProcessFilesystemPolicy _filesystemPolicy;

    public FilesystemGovernedOutOfProcessRuntime(
        PinnedOutOfProcessRuntime runtime,
        OutOfProcessFilesystemPolicy filesystemPolicy)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _filesystemPolicy = (filesystemPolicy ?? throw new ArgumentNullException(nameof(filesystemPolicy))).Validate();
    }

    public Task<OutOfProcessExecutionResult> ExecuteAsync(
        OutOfProcessExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (_filesystemPolicy.RequiresIsolation)
        {
            throw new InvalidOperationException(
                $"Filesystem scope '{_filesystemPolicy.Scope}' requires an enforcement backend. " +
                "The current pinned local-process runtime only verifies working-directory containment and will not launch with broader host filesystem authority.");
        }

        return _runtime.ExecuteAsync(request, cancellationToken);
    }
}
