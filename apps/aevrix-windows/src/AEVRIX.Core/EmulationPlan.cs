namespace Aevrix.Core;

public enum EmulationIsolationLevel
{
    ProcessRestricted,
    DisposableSandbox,
    DisposableVirtualMachine
}

public enum EmulationNetworkPolicy
{
    Disabled,
    AllowlistOnly
}

public enum EmulationTestKind
{
    Install,
    FirstLaunch,
    UiWorkflow,
    FileSystemObservation,
    ProcessObservation,
    NetworkObservation,
    Upgrade,
    Repair,
    Uninstall
}

public sealed record EmulationTestStep(
    string Id,
    EmulationTestKind Kind,
    TimeSpan Timeout,
    IReadOnlyList<string> DependsOn)
{
    public void Validate()
    {
        WorkspaceScope.ValidateToken(Id, nameof(Id));
        ArgumentNullException.ThrowIfNull(DependsOn);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(4))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Each emulation step must have a timeout between zero and four hours.");
        }
        if (DependsOn.Any(string.IsNullOrWhiteSpace) || DependsOn.Contains(Id, StringComparer.Ordinal))
        {
            throw new ArgumentException("Emulation dependencies must be non-blank and cannot reference the step itself.", nameof(DependsOn));
        }
    }
}

public sealed record EmulationPlan(
    Guid InvestigationId,
    InvestigationTargetKind TargetKind,
    EmulationIsolationLevel IsolationLevel,
    EmulationNetworkPolicy NetworkPolicy,
    IReadOnlyList<string> NetworkAllowlist,
    IReadOnlyList<InvestigationInputArtifact> Artifacts,
    IReadOnlyList<EmulationTestStep> Steps,
    bool ElevationExplicitlyApproved,
    bool DestructiveHostChangesApproved)
{
    public void Validate()
    {
        if (!InvestigationDraft.SupportsEmulation(TargetKind))
        {
            throw new InvalidOperationException("Emulation is limited to executable application targets.");
        }
        if (Artifacts.Count == 0)
        {
            throw new InvalidOperationException("Emulation requires at least one installer, executable or package artifact.");
        }
        if (Steps.Count == 0)
        {
            throw new InvalidOperationException("Emulation requires at least one governed test step.");
        }
        if (IsolationLevel == EmulationIsolationLevel.ProcessRestricted && DestructiveHostChangesApproved)
        {
            throw new InvalidOperationException("Destructive host changes require a disposable sandbox or virtual machine.");
        }
        if (NetworkPolicy == EmulationNetworkPolicy.Disabled && NetworkAllowlist.Count != 0)
        {
            throw new InvalidOperationException("A disabled network policy cannot contain an allowlist.");
        }
        if (NetworkPolicy == EmulationNetworkPolicy.AllowlistOnly && NetworkAllowlist.Count == 0)
        {
            throw new InvalidOperationException("Allowlist-only networking requires at least one explicitly allowed host.");
        }
        if (NetworkAllowlist.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('/')))
        {
            throw new ArgumentException("Network allowlist entries must be host names only, without paths.", nameof(NetworkAllowlist));
        }

        var ids = Steps.Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in Steps)
        {
            step.Validate();
            foreach (var dependency in step.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    throw new InvalidOperationException($"Unknown emulation dependency '{dependency}'.");
                }
            }
        }
    }

    public static EmulationPlan CreateDefault(
        Guid investigationId,
        InvestigationTargetKind targetKind,
        IReadOnlyList<InvestigationInputArtifact> artifacts)
    {
        if (!InvestigationDraft.SupportsEmulation(targetKind))
        {
            throw new ArgumentException("Default emulation plan requires an executable application target.", nameof(targetKind));
        }

        var steps = new[]
        {
            new EmulationTestStep("install", EmulationTestKind.Install, TimeSpan.FromMinutes(15), Array.Empty<string>()),
            new EmulationTestStep("first-launch", EmulationTestKind.FirstLaunch, TimeSpan.FromMinutes(10), new[] { "install" }),
            new EmulationTestStep("process-observation", EmulationTestKind.ProcessObservation, TimeSpan.FromMinutes(10), new[] { "first-launch" }),
            new EmulationTestStep("filesystem-observation", EmulationTestKind.FileSystemObservation, TimeSpan.FromMinutes(10), new[] { "first-launch" }),
            new EmulationTestStep("repair", EmulationTestKind.Repair, TimeSpan.FromMinutes(15), new[] { "process-observation", "filesystem-observation" }),
            new EmulationTestStep("uninstall", EmulationTestKind.Uninstall, TimeSpan.FromMinutes(15), new[] { "repair" })
        };

        var plan = new EmulationPlan(
            investigationId,
            targetKind,
            EmulationIsolationLevel.DisposableSandbox,
            EmulationNetworkPolicy.Disabled,
            Array.Empty<string>(),
            artifacts,
            steps,
            ElevationExplicitlyApproved: false,
            DestructiveHostChangesApproved: false);
        plan.Validate();
        return plan;
    }
}
