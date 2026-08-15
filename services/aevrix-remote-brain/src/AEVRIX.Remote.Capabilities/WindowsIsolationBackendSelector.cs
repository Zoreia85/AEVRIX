namespace Aevrix.Remote.Capabilities;

public enum WindowsIsolationBackendKind
{
    None,
    LocalUnrestricted,
    ZeroCapabilityAppContainer,
    ExperimentalProcessSandbox
}

public sealed record WindowsIsolationBackendSelection(
    WindowsIsolationBackendKind Backend,
    bool LaunchEligible,
    string DecisionCode)
{
    public WindowsIsolationBackendSelection Validate()
    {
        if (!Enum.IsDefined(Backend))
        {
            throw new ArgumentOutOfRangeException(nameof(Backend));
        }
        if (string.IsNullOrWhiteSpace(DecisionCode)
            || DecisionCode.Length > 128
            || DecisionCode.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Isolation backend decision code is invalid.", nameof(DecisionCode));
        }
        if (LaunchEligible == (Backend == WindowsIsolationBackendKind.None))
        {
            throw new InvalidOperationException("Isolation backend selection eligibility is inconsistent with the selected backend.");
        }
        return this;
    }
}

/// <summary>
/// Pure fail-closed eligibility selector for Windows process-isolation backends.
/// It does not launch a process and does not convert feature availability into authority.
/// Experimental process sandbox selection requires both the reviewed governance opt-in and
/// native feature availability. The existing zero-capability AppContainer backend remains
/// eligible only for Network=None + Filesystem=Unrestricted. Filesystem-restricted profiles
/// are denied unless the experimental backend is both available and explicitly enabled.
/// </summary>
public sealed class WindowsIsolationBackendSelector
{
    private readonly ExperimentalProcessSandboxGovernancePolicy _experimentalPolicy;

    public WindowsIsolationBackendSelector(ExperimentalProcessSandboxGovernancePolicy experimentalPolicy)
    {
        _experimentalPolicy = (experimentalPolicy ?? throw new ArgumentNullException(nameof(experimentalPolicy))).Validate();
    }

    public WindowsIsolationBackendSelection Select(
        OutOfProcessAuthorityPolicy authority,
        WindowsExperimentalProcessSandboxCapability experimentalCapability)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(experimentalCapability);
        authority.Validate();

        if (!OperatingSystem.IsWindows())
        {
            return Deny("WindowsIsolationBackendUnavailableOnHost");
        }

        if (!authority.Network.RequiresIsolation && !authority.Filesystem.RequiresIsolation)
        {
            return Allow(WindowsIsolationBackendKind.LocalUnrestricted, "AuthorizedUnrestrictedLocalProcess");
        }

        if (_experimentalPolicy.AllowsUse(experimentalCapability))
        {
            return Allow(WindowsIsolationBackendKind.ExperimentalProcessSandbox, "AuthorizedExperimentalProcessSandbox");
        }

        if (authority.Network.Scope == OutOfProcessNetworkScope.None
            && authority.Filesystem.Scope == OutOfProcessFilesystemScope.Unrestricted)
        {
            return Allow(WindowsIsolationBackendKind.ZeroCapabilityAppContainer, "AuthorizedZeroCapabilityAppContainer");
        }

        if (authority.Filesystem.RequiresIsolation)
        {
            return Deny(experimentalCapability.FullyAvailable
                ? "ExperimentalFilesystemIsolationNotGovernanceAuthorized"
                : "FilesystemIsolationBackendUnavailable");
        }

        return Deny("NetworkIsolationBackendUnavailable");
    }

    private static WindowsIsolationBackendSelection Allow(WindowsIsolationBackendKind backend, string code) =>
        new WindowsIsolationBackendSelection(backend, true, code).Validate();

    private static WindowsIsolationBackendSelection Deny(string code) =>
        new WindowsIsolationBackendSelection(WindowsIsolationBackendKind.None, false, code).Validate();
}
