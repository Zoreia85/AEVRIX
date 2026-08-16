using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class SubjectProjectWorkspaceBoundAdapterTests
{
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task SameProject_DifferentSubjectsReceiveDifferentOpaqueNamespaces()
    {
        using var temp = new TempDir();
        var first = new RecordingProvider();
        var second = new RecordingProvider();
        var manager = new ProjectWorkspaceLeaseManager(new(temp.Path));

        var alpha = new SubjectProjectWorkspaceBoundAdapter(
            first, manager, new FixedWorkspaceSubjectResolver("user-alpha"));
        var beta = new SubjectProjectWorkspaceBoundAdapter(
            second, manager, new FixedWorkspaceSubjectResolver("user-beta"));

        await alpha.ExecuteAsync(Context(), Envelope());
        await beta.ExecuteAsync(Context(), Envelope());

        Assert.AreEqual("user-alpha", first.SubjectId);
        Assert.AreEqual("user-beta", second.SubjectId);
        Assert.AreNotEqual(first.RootPath, second.RootPath);
        Assert.IsFalse(first.RootPath!.Contains("user-alpha", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(second.RootPath!.Contains("user-beta", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(Directory.Exists(first.RootPath));
        Assert.IsFalse(Directory.Exists(second.RootPath));
    }

    [TestMethod]
    public async Task SubjectResolver_InvalidIdentityFailsBeforeProviderExecution()
    {
        using var temp = new TempDir();
        var provider = new RecordingProvider();
        var adapter = new SubjectProjectWorkspaceBoundAdapter(
            provider,
            new ProjectWorkspaceLeaseManager(new(temp.Path)),
            new FixedWorkspaceSubjectResolver("../escape"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.ExecuteAsync(Context(), Envelope()));

        Assert.IsFalse(provider.Executed);
    }

    [TestMethod]
    public async Task SubjectScopedReadOnlyLeaseCannotBeUpgradedToWrite()
    {
        using var temp = new TempDir();
        var provider = new RecordingProvider(attemptWrite: true);
        var adapter = new SubjectProjectWorkspaceBoundAdapter(
            provider,
            new ProjectWorkspaceLeaseManager(new(temp.Path)),
            new FixedWorkspaceSubjectResolver("user-alpha"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.ExecuteAsync(Context(), Envelope()));

        Assert.IsNotNull(provider.RootPath);
        Assert.IsFalse(Directory.Exists(provider.RootPath));
    }

    private static SpecialistAdapterExecutionEnvelope Envelope() =>
        new(AdapterNetworkScope.None, AdapterWorkspaceScope.ReadOnly, AgentIsolationLevel.Container, 4096, 16, 16);

    private static SpecialistExecutionContext Context() => new(
        "mission-subject",
        Project,
        "target-1",
        new MissionTaskSpec(
            "task-subject",
            MissionSpecialistKind.StaticAnalysis,
            "Authorized subject workspace isolation test.",
            ["ev-1"],
            []),
        new Dictionary<string, SpecialistTaskResult>());

    private sealed class RecordingProvider(bool attemptWrite = false)
        : IProjectWorkspaceAwareMissionSpecialistProviderAdapter
    {
        public string ProviderId => "subject-test-provider";
        public MissionSpecialistKind Kind => MissionSpecialistKind.StaticAnalysis;
        public SpecialistAdapterExecutionProfile ExecutionProfile { get; } =
            new(AdapterNetworkScope.None, AdapterWorkspaceScope.ReadOnly, AgentIsolationLevel.Container);

        public string? RootPath { get; private set; }
        public string? SubjectId { get; private set; }
        public bool Executed { get; private set; }

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Envelope and lease required.");

        public Task<SpecialistExecutionOutput> ExecuteAsync(
            SpecialistExecutionContext context,
            SpecialistAdapterExecutionEnvelope envelope,
            ProjectWorkspaceLease workspace,
            CancellationToken cancellationToken = default)
        {
            Executed = true;
            RootPath = workspace.RootPath;
            SubjectId = workspace.SubjectId;
            if (attemptWrite)
            {
                workspace.EnsureWritable();
            }

            return Task.FromResult(new SpecialistExecutionOutput(
                "subject workspace verified",
                1.0,
                context.Task.EvidenceIds,
                ["artifact-subject"]));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-subject-ws-tests",
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
