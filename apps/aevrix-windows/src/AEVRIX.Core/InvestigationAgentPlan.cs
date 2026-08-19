namespace Aevrix.Core;

public enum InvestigationAgentRole
{
    Coordinator,
    Acquisition,
    StaticAnalyzer,
    DynamicObserver,
    EvidenceVerifier,
    BlueprintSynthesizer,
    CleanRoomBuilder,
    DifferentialJudge,
    QualityAssurance
}

public enum AgentWorkPackageState
{
    Planned,
    Ready,
    Running,
    Paused,
    Blocked,
    Failed,
    Verified,
    Cancelled
}

public sealed record AgentWorkPackage(
    string Id,
    InvestigationAgentRole Role,
    InvestigationPhase Phase,
    IReadOnlyList<string> DependsOn,
    bool RequiresVerifiedEvidence,
    AgentWorkPackageState State)
{
    public void Validate()
    {
        WorkspaceScope.ValidateToken(Id, nameof(Id));
        ArgumentNullException.ThrowIfNull(DependsOn);
        if (DependsOn.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Work-package dependencies cannot be blank.", nameof(DependsOn));
        }
        if (DependsOn.Contains(Id, StringComparer.Ordinal))
        {
            throw new ArgumentException("A work package cannot depend on itself.", nameof(DependsOn));
        }
    }
}

public sealed record InvestigationAgentPlan(
    InvestigationStrategy Strategy,
    InvestigationTargetKind TargetKind,
    IReadOnlyList<AgentWorkPackage> WorkPackages)
{
    public static InvestigationAgentPlan Create(
        InvestigationStrategy strategy,
        InvestigationTargetKind targetKind)
    {
        if (strategy is InvestigationStrategy.InvestigateAndEmulate &&
            !InvestigationDraft.SupportsEmulation(targetKind))
        {
            throw new ArgumentException("Emulation requires an executable application target.", nameof(targetKind));
        }

        var packages = new List<AgentWorkPackage>
        {
            Package("coordination", InvestigationAgentRole.Coordinator, InvestigationPhase.IntakeAndAuthorization),
            Package("acquisition", InvestigationAgentRole.Acquisition, InvestigationPhase.Acquisition, "coordination"),
            Package("static-analysis", InvestigationAgentRole.StaticAnalyzer, InvestigationPhase.StaticAnalysis, "acquisition"),
            Package("evidence-verification", InvestigationAgentRole.EvidenceVerifier, InvestigationPhase.EvidenceCorrelation, "static-analysis")
        };

        if (strategy is InvestigationStrategy.InvestigateAndEmulate or InvestigationStrategy.InvestigateAndBuildParallel)
        {
            if (InvestigationDraft.SupportsEmulation(targetKind))
            {
                packages.Insert(3, Package(
                    "dynamic-observation",
                    InvestigationAgentRole.DynamicObserver,
                    InvestigationPhase.DynamicObservation,
                    "acquisition"));
                packages[packages.FindIndex(item => item.Id == "evidence-verification")] = Package(
                    "evidence-verification",
                    InvestigationAgentRole.EvidenceVerifier,
                    InvestigationPhase.EvidenceCorrelation,
                    "static-analysis",
                    "dynamic-observation");
            }
        }

        packages.Add(Package(
            "blueprint",
            InvestigationAgentRole.BlueprintSynthesizer,
            InvestigationPhase.BlueprintSynthesis,
            "evidence-verification"));

        if (strategy is InvestigationStrategy.InvestigateAndBuildParallel or InvestigationStrategy.ReconstructWhiteLabel)
        {
            packages.Add(Package(
                "clean-room-build",
                InvestigationAgentRole.CleanRoomBuilder,
                InvestigationPhase.Reconstruction,
                requiresVerifiedEvidence: true,
                "evidence-verification"));
            packages.Add(Package(
                "differential-judge",
                InvestigationAgentRole.DifferentialJudge,
                InvestigationPhase.DifferentialValidation,
                requiresVerifiedEvidence: true,
                "blueprint",
                "clean-room-build"));
            packages.Add(Package(
                "final-qa",
                InvestigationAgentRole.QualityAssurance,
                InvestigationPhase.FinalQualityAssurance,
                "differential-judge"));
        }
        else
        {
            packages.Add(Package(
                "final-qa",
                InvestigationAgentRole.QualityAssurance,
                InvestigationPhase.FinalQualityAssurance,
                "blueprint"));
        }

        foreach (var package in packages)
        {
            package.Validate();
        }
        ValidateDependencies(packages);
        return new InvestigationAgentPlan(strategy, targetKind, packages);
    }

    public IReadOnlyList<AgentWorkPackage> GetReadyPackages(
        IReadOnlyDictionary<string, AgentWorkPackageState> states,
        IReadOnlySet<string> verifiedEvidenceBoundPackages)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(verifiedEvidenceBoundPackages);

        var ready = new List<AgentWorkPackage>();
        foreach (var package in WorkPackages)
        {
            var state = states.TryGetValue(package.Id, out var current)
                ? current
                : package.State;
            if (state is not (AgentWorkPackageState.Planned or AgentWorkPackageState.Ready))
            {
                continue;
            }

            var dependenciesVerified = package.DependsOn.All(dependency =>
                states.TryGetValue(dependency, out var dependencyState)
                && dependencyState == AgentWorkPackageState.Verified);
            if (!dependenciesVerified)
            {
                continue;
            }

            if (package.RequiresVerifiedEvidence &&
                !package.DependsOn.All(verifiedEvidenceBoundPackages.Contains))
            {
                continue;
            }

            ready.Add(package with { State = AgentWorkPackageState.Ready });
        }
        return ready;
    }

    private static AgentWorkPackage Package(
        string id,
        InvestigationAgentRole role,
        InvestigationPhase phase,
        params string[] dependencies)
        => Package(id, role, phase, false, dependencies);

    private static AgentWorkPackage Package(
        string id,
        InvestigationAgentRole role,
        InvestigationPhase phase,
        bool requiresVerifiedEvidence,
        params string[] dependencies)
        => new(
            id,
            role,
            phase,
            dependencies,
            requiresVerifiedEvidence,
            dependencies.Length == 0 ? AgentWorkPackageState.Ready : AgentWorkPackageState.Planned);

    private static void ValidateDependencies(IReadOnlyCollection<AgentWorkPackage> packages)
    {
        var ids = packages.Select(package => package.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            foreach (var dependency in package.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    throw new InvalidOperationException($"Unknown work-package dependency '{dependency}'.");
                }
            }
        }
    }
}
