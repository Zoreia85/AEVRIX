using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class ProjectWorkspaceBoundAdapterRegressionTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public async Task FreshLeases_AreProjectBoundAndDestroyed()
    {
        using var temp = new TempDir();
        var stub = new Stub(AdapterWorkspaceScope.ReadOnly);
        var adapter = new ProjectWorkspaceBoundAdapter(
            stub, new ProjectWorkspaceLeaseManager(new(temp.Path)));

        await adapter.ExecuteAsync(Context(A), Envelope(AdapterWorkspaceScope.ReadOnly));
        await adapter.ExecuteAsync(Context(B), Envelope(AdapterWorkspaceScope.ReadOnly));

        Assert.AreEqual(2, stub.Roots.Count);
        Assert.AreNotEqual(Path.GetDirectoryName(stub.Roots[0]), Path.GetDirectoryName(stub.Roots[1]));
        Assert.IsTrue(stub.SawExistingRoot);
        Assert.IsTrue(stub.Roots.All(path => !Directory.Exists(path)));
        Assert.IsFalse(stub.Roots[0].Contains(A.ToString("D"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ReadOnlyLease_CannotBeUpgradedToWrite()
    {
        using var temp = new TempDir();
        var stub = new Stub(AdapterWorkspaceScope.ReadOnly, write: true);
        var adapter = new ProjectWorkspaceBoundAdapter(
            stub, new ProjectWorkspaceLeaseManager(new(temp.Path)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.ExecuteAsync(Context(A), Envelope(AdapterWorkspaceScope.ReadOnly)));

        Assert.IsTrue(stub.Roots.All(path => !Directory.Exists(path)));
    }

    private static SpecialistAdapterExecutionEnvelope Envelope(AdapterWorkspaceScope scope) =>
        new(AdapterNetworkScope.None, scope, AgentIsolationLevel.Container, 4096, 16, 16);

    private static SpecialistExecutionContext Context(Guid projectId) => new(
        "mission-ws", projectId, "target-1",
        new("task-ws", MissionSpecialistKind.StaticAnalysis, "Authorized workspace test.", ["ev-1"], []),
        new Dictionary<string, SpecialistTaskResult>());

    private sealed class Stub(AdapterWorkspaceScope scope, bool write = false)
        : IProjectWorkspaceAwareMissionSpecialistProviderAdapter
    {
        public string ProviderId => "test-provider";
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;
        public SpecialistAdapterExecutionProfile ExecutionProfile { get; } =
            new(AdapterNetworkScope.None, scope, AgentIsolationLevel.Container);
        public List<string> Roots { get; } = [];
        public bool SawExistingRoot { get; private set; }

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Envelope and lease required.");

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            SpecialistAdapterExecutionEnvelope envelope,
            ProjectWorkspaceLease workspace,
            CancellationToken cancellationToken = default)
        {
            Roots.Add(workspace.RootPath);
            SawExistingRoot |= Directory.Exists(workspace.RootPath);
            if (write) workspace.EnsureWritable();
            return Task.FromResult(new SpecialistExecutionOutput(
                "ok", .9, context.Task.EvidenceIds, ["artifact-1"]));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-ws-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
