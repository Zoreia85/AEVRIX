using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aevrix.Remote.Capabilities;

public sealed record WindowsExperimentalProcessSandboxCapability(
    bool ModulePresent,
    bool CreateProcessInSandboxAvailable,
    bool CreateProcessAsUserInSandboxAvailable,
    string ContractVersion,
    bool Experimental,
    string? ModulePath)
{
    public bool FullyAvailable =>
        ModulePresent
        && CreateProcessInSandboxAvailable
        && CreateProcessAsUserInSandboxAvailable;
}

/// <summary>
/// Explicit governance gate for Microsoft's experimental process sandbox API.
/// Availability alone never authorizes execution. AEVRIX must opt in to the exact
/// contract version after independent review and can disable the path without affecting
/// the existing AppContainer/Job Object runtime.
/// </summary>
public sealed record ExperimentalProcessSandboxGovernancePolicy(
    bool Enabled = false,
    string ApprovedContractVersion = WindowsExperimentalProcessSandboxProbe.KnownContractVersion)
{
    public ExperimentalProcessSandboxGovernancePolicy Validate()
    {
        if (!string.Equals(
                ApprovedContractVersion,
                WindowsExperimentalProcessSandboxProbe.KnownContractVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Only reviewed experimental sandbox contract '{WindowsExperimentalProcessSandboxProbe.KnownContractVersion}' is accepted.",
                nameof(ApprovedContractVersion));
        }

        return this;
    }

    public bool AllowsUse(WindowsExperimentalProcessSandboxCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        Validate();
        return Enabled
            && capability.Experimental
            && capability.FullyAvailable
            && string.Equals(capability.ContractVersion, ApprovedContractVersion, StringComparison.Ordinal);
    }
}

/// <summary>
/// Feature-detects the experimental Windows CreateProcessInSandbox surface without invoking it.
/// processmodel.dll is resolved only from the Windows System directory; current-directory or PATH
/// shadowing is never consulted. This probe is capability metadata, not Evidence and not permission
/// to execute a sandboxed adapter.
/// </summary>
public sealed class WindowsExperimentalProcessSandboxProbe
{
    public const string KnownContractVersion = "0.1.0";
    public const string CreateProcessExport = "Experimental_CreateProcessInSandbox";
    public const string CreateProcessAsUserExport = "Experimental_CreateProcessAsUserInSandbox";

    [SupportedOSPlatform("windows")]
    public WindowsExperimentalProcessSandboxCapability Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Experimental Windows process sandbox probing requires Windows.");
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(systemDirectory) || !Path.IsPathFullyQualified(systemDirectory))
        {
            throw new InvalidOperationException("Windows System directory could not be resolved safely.");
        }

        var modulePath = Path.GetFullPath(Path.Combine(systemDirectory, "processmodel.dll"));
        if (!File.Exists(modulePath))
        {
            return new WindowsExperimentalProcessSandboxCapability(
                ModulePresent: false,
                CreateProcessInSandboxAvailable: false,
                CreateProcessAsUserInSandboxAvailable: false,
                ContractVersion: KnownContractVersion,
                Experimental: true,
                ModulePath: null);
        }

        if ((File.GetAttributes(modulePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Windows processmodel.dll cannot be accepted through a reparse point.");
        }

        if (!NativeLibrary.TryLoad(modulePath, out var module) || module == IntPtr.Zero)
        {
            return new WindowsExperimentalProcessSandboxCapability(
                ModulePresent: true,
                CreateProcessInSandboxAvailable: false,
                CreateProcessAsUserInSandboxAvailable: false,
                ContractVersion: KnownContractVersion,
                Experimental: true,
                ModulePath: modulePath);
        }

        try
        {
            var createAvailable = NativeLibrary.TryGetExport(module, CreateProcessExport, out var create)
                && create != IntPtr.Zero;
            var createAsUserAvailable = NativeLibrary.TryGetExport(module, CreateProcessAsUserExport, out var createAsUser)
                && createAsUser != IntPtr.Zero;

            return new WindowsExperimentalProcessSandboxCapability(
                ModulePresent: true,
                CreateProcessInSandboxAvailable: createAvailable,
                CreateProcessAsUserInSandboxAvailable: createAsUserAvailable,
                ContractVersion: KnownContractVersion,
                Experimental: true,
                ModulePath: modulePath);
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }
}
