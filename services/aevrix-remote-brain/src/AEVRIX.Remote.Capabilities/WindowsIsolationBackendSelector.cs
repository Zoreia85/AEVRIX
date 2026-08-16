namespace Aevrix.Remote.Capabilities;

public enum WindowsIsolationBackendKind
{
    None,
    LocalUnrestricted,
    ZeroCapabilityAppContainer,
    ExperimentalProcessSandboxCandidate
}

public sealed record WindowsIsolationBackendSelection(
    WindowsIsolationBackendKind Backend,
    bool PolicyEligible,
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
        if (PolicyEligible == (Backend == WindowsIsolationBackendKind.None))
        {
            throw new InvalidOperationException("Isolation backend policy eligibility is inconsistent with the selected backend.");
        }
        return this;
    }
}

/// <summary>
/// Pure fail-closed policy-eligibility selector for Windows process-isolation backends.
/// It never launches a process and its result is not execution authority. GovernedOutOfProcessRuntime
/// remains the launch authority and still requires a registered backend plus post-execution attestation.
/// Experimental process sandbox candidacy requires the reviewed governance opt-in and native feature
/// availability. The existing zero-capability AppContainer backend remains eligible only for
/// Network=None + Filesystem=Unrestricted. Filesystem-restricted profiles are otherwise denied.
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
            return Eligible(WindowsIsolationBackendKind.LocalUnrestricted, "EligibleUnrestrictedLocalProcess");
        }

        if (_experimentalPolicy.AllowsUse(experimentalCapability))
        {
            return Eligible(
                WindowsIsolationBackendKind.ExperimentalProcessSandboxCandidate,
                "EligibleExperimentalProcessSandboxCandidate");
        }

        if (authority.Network.Scope == OutOfProcessNetworkScope.None
            && authority.Filesystem.Scope == OutOfProcessFilesystemScope.Unrestricted)
        {
            return Eligible(
                WindowsIsolationBackendKind.ZeroCapabilityAppContainer,
                "EligibleZeroCapabilityAppContainer");
        }

        if (authority.Filesystem.RequiresIsolation)
        {
            return Deny(experimentalCapability.FullyAvailable
                ? "ExperimentalFilesystemIsolationNotGovernanceAuthorized"
                : "FilesystemIsolationBackendUnavailable");
        }

        return Deny("NetworkIsolationBackendUnavailable");
    }

    private static WindowsIsolationBackendSelection Eligible(WindowsIsolationBackendKind backend, string code) =>
        new WindowsIsolationBackendSelection(backend, true, code).Validate();

    private static WindowsIsolationBackendSelection Deny(string code) =>
        new WindowsIsolationBackendSelection(WindowsIsolationBackendKind.None, false, code).Validate();
}
