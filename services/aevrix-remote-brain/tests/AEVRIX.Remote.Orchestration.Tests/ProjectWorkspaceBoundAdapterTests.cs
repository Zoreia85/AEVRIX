using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ProjectWorkspaceBoundAdapterTests
{
    [TestMethod]
    public async Task ExecuteAsync_UsesFreshProjectBoundWorkspaceAndDestroysItAfterSuccess()
    {
        using var root = new TemporaryDirectory();
        var manager = new ProjectWorkspaceLeaseManager(new ProjectWorkspaceLeaseOptions(root.Path));
        var inner = new WorkspaceAdapter("provider-a", shouldFail: false, writeFile: true);
        var adapter = new ProjectWorkspaceBoundAdapter(inner, manager);
        var context = Context(ProjectA);

        var result = await adapter.ExecuteAsync(context, Envelope(AdapterWorkspaceScope.ReadWrite));

        Assert.AreEqual(.93, result.Confidence, .0001);
        Assert.IsNotNull(inner.ObservedRoot);
        Assert.IsTrue(inner.SawWorkspaceDuringExecution);
        Assert.IsFalse(Directory.Exists(inner.ObservedRoot));
        Assert.IsFalse(inner.ObservedRoot.Contains(ProjectA.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExecuteAsync_DestroysWorkspaceWhenProviderFails()
    {
        using var root = new TemporaryDirectory();
        var manager = new ProjectWorkspaceLeaseManager(new ProjectWorkspaceLeaseOptions(root.Path));
        var inner = new WorkspaceAdapter("provider-a", shouldFail: true, writeFile: true);
        var adapter = new ProjectWorkspaceBoundAdapter(inner, manager);

        await Assert.ThrowsAsync<IOException>(() =>
            adapter.ExecuteAsync(Context(ProjectA), Envelope(AdapterWorkspaceScope.ReadWrite)));

        Assert.IsNotNull(inner.ObservedRoot);
        Assert.IsFalse(Directory.Exists(inner.ObservedRoot));
    }

    [TestMethod]
    public async Task ExecuteAsync_ReadOnlyLeaseCannotBeUpgradedByProvider()
    {
        using var root = new TemporaryDirectory();
        var manager = new ProjectWorkspaceLeaseManager(new ProjectWorkspaceLeaseOptions(root.Path));
        var inner = new WorkspaceAdapter("provider-a", shouldFail: false, writeFile: true);
        var adapter = new ProjectWorkspaceBoundAdapter(inner, manager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.ExecuteAsync(Context(ProjectA), Envelope(AdapterWorkspaceScope.ReadOnly)));

        Assert.IsNotNull(inner.ObservedRoot);
        Assert.IsFalse(Directory.Exists(inner.ObservedRoot));
    }

    [TestMethod]
    public async Task ExecuteAsync_SameWorkGetsFreshLeaseAndProjectsDoNotShareRoots()
    {
        using var root = new TemporaryDirectory();
        var manager = new ProjectWorkspaceLeaseManager(new ProjectWorkspaceLeaseOptions(root.Path));
        var inner = new WorkspaceAdapter("provider-a", shouldFail: false, writeFile: false);
        var adapter = new ProjectWorkspaceBoundAdapter(inner, manager);

        await adapter.ExecuteAsync(Context(ProjectA), Envelope(AdapterWorkspaceScope.ReadOnly));
        var first = inner.ObservedRoots[^1];
        await adapter.ExecuteAsync(Context(ProjectA), Envelope(AdapterWorkspaceScope.ReadOnly));
        var second = inner.ObservedRoots[^1];
        await adapter.ExecuteAsync(Context(ProjectB), Envelope(AdapterWorkspaceScope.ReadOnly));
        var third = inner.ObservedRoots[^1];

        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(second, third);
        Assert.AreNotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(third));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsWorkspaceNoneBeforeProviderRuns()
    {
        using var root = new TemporaryDirectory();
        var manager = new ProjectWorkspaceLeaseManager(new ProjectWorkspaceLeaseOptions(root.Path));
        var inner = new WorkspaceAdapter("provider-a", shouldFail: false, writeFile: false);
        var adapter = new ProjectWorkspaceBoundAdapter(inner, manager);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ExecuteAsync(Context(ProjectA), Envelope(AdapterWorkspaceScope.None)));

        Assert.AreEqual(0, inner.CallCount);
    }

    private static readonly Guid ProjectA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProjectB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static SpecialistAdapterExecutionEnvelope Envelope(AdapterWorkspaceScope workspace) => new(
        AdapterNetworkScope.None,
        workspace,
        AgentIsolationLevel.Container,
        MaximumSummaryUtf8Bytes: 8_000,
        MaximumEvidenceIds: 32,
        MaximumArtifactIds: 32);

    private static SpecialistExecutionContext Context(Guid projectId) => new(
        "mission-workspace",
        projectId,
        "target-001",
        new MissionTaskSpec(
            "static-task",
            MissionSpecialistKind.StaticAnalysis,
            "Analyze authorized evidence in an ephemeral project workspace.",
            ["ev-001"],
            []),
        new Dictionary<string, SpecialistTaskResult>());

    private sealed class WorkspaceAdapter(
        string providerId,
        bool shouldFail,
        bool writeFile) : IProjectWorkspaceAwareMissionSpecialistProviderAdapter
    {
        public string ProviderId => providerId;
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;
        public SpecialistAdapterExecutionProfile ExecutionProfile { get; } = new(
            AdapterNetworkScope.None,
            AdapterWorkspaceScope.ReadWrite,
            AgentIsolationLevel.Container);
        public string? ObservedRoot { get; private set; }
        public List<string> ObservedRoots { get; } = [];
        public bool SawWorkspaceDuringExecution { get; private set; }
        public int CallCount { get; private set; }

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Workspace-aware adapter requires an execution envelope and lease.");

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            SpecialistAdapterExecutionEnvelope envelope,
            ProjectWorkspaceLease workspace,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            ObservedRoot = workspace.RootPath;
            ObservedRoots.Add(workspace.RootPath);
            SawWorkspaceDuringExecution = Directory.Exists(workspace.RootPath);

            if (writeFile)
            {
                workspace.EnsureWritable();
                var path = workspace.ResolveRelativePath("scratch/result.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "workspace-scoped test material");
            }

            if (shouldFail)
            {
                throw new IOException("simulated provider failure");
            }

            return Task.FromResult(new SpecialistExecutionOutput(
                "Completed workspace-scoped analysis.",
                .93,
                context.Task.EvidenceIds,
                ["artifact-workspace"]));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-workspace-bound-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
