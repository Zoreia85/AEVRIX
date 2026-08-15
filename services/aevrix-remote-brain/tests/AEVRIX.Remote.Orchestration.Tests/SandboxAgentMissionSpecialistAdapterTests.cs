using System.Net;
using System.Text;
using System.Text.Json;
using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class SandboxAgentMissionSpecialistAdapterTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task ExecuteAsync_UsesProjectLeaseAndReturnsManifestArtifact()
    {
        using var temp = new TempDir();
        var handler = new AgentHandler(evidenceIds: ["ev-1"]);
        var adapter = BuildAdapter(temp.Path, handler);
        var wrapper = new ProjectWorkspaceBoundAdapter(
            adapter,
            new ProjectWorkspaceLeaseManager(new(temp.Path)));

        var output = await wrapper.ExecuteAsync(Context(), Envelope());

        Assert.AreEqual("sandbox analysis complete", output.Summary);
        CollectionAssert.AreEqual(new[] { "ev-1" }, output.EvidenceIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "manifest:" + new string('a', 64) },
            output.ArtifactIds.ToArray());
        Assert.IsNotNull(handler.ObservedProjectRoot);
        Assert.IsFalse(Directory.Exists(handler.ObservedProjectRoot));
        Assert.IsFalse(handler.ObservedProjectRoot.Contains(ProjectId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(handler.PolicyHostFilesystemMounted is false);
        Assert.IsTrue(handler.PolicyOutboundNetworkAllowed is false);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsEvidenceOutsideGovernedBoundary()
    {
        using var temp = new TempDir();
        var handler = new AgentHandler(evidenceIds: ["ev-1", "ev-forged"]);
        var wrapper = new ProjectWorkspaceBoundAdapter(
            BuildAdapter(temp.Path, handler),
            new ProjectWorkspaceLeaseManager(new(temp.Path)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            wrapper.ExecuteAsync(Context(), Envelope()));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsIsolationAttestationThatBroadensHostAccess()
    {
        using var temp = new TempDir();
        var handler = new AgentHandler(evidenceIds: ["ev-1"], hostFilesystemMounted: true);
        var wrapper = new ProjectWorkspaceBoundAdapter(
            BuildAdapter(temp.Path, handler),
            new ProjectWorkspaceLeaseManager(new(temp.Path)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            wrapper.ExecuteAsync(Context(), Envelope()));
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsNonSuccessfulTerminalState()
    {
        using var temp = new TempDir();
        var handler = new AgentHandler(evidenceIds: ["ev-1"], finalState: AgentJobState.Failed);
        var wrapper = new ProjectWorkspaceBoundAdapter(
            BuildAdapter(temp.Path, handler),
            new ProjectWorkspaceLeaseManager(new(temp.Path)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrapper.ExecuteAsync(Context(), Envelope()));
    }

    private static SandboxAgentMissionSpecialistAdapter BuildAdapter(
        string allowedRoot,
        HttpMessageHandler handler)
    {
        var source = new CapabilitySource(
            "OpenHands/OpenHands",
            "MIT",
            new string('b', 40),
            new string('c', 64));
        var backend = new AgentBackendDescriptor(
            "sandbox-static",
            new Uri("http://127.0.0.1:47123/"),
            source,
            AgentIsolationLevel.Container,
            CapabilityApprovalState.Approved,
            [allowedRoot],
            HostFilesystemMounted: false,
            OutboundNetworkAllowed: false);
        HttpClient http = new(handler);
        var client = new SandboxAgentBackendClient(
            http,
            backend,
            new SandboxAgentBackendClientOptions(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMinutes(1)));

        return new SandboxAgentMissionSpecialistAdapter(
            MissionSpecialistKind.StaticAnalysis,
            client,
            new SpecialistAdapterExecutionProfile(
                AdapterNetworkScope.None,
                AdapterWorkspaceScope.ReadWrite,
                AgentIsolationLevel.Container),
            new SandboxAgentMissionSpecialistAdapterOptions(TimeSpan.FromMilliseconds(10), 3));
    }

    private static SpecialistExecutionContext Context() => new(
        "mission-agent",
        ProjectId,
        "target-1",
        new MissionTaskSpec(
            "task-static",
            MissionSpecialistKind.StaticAnalysis,
            "Analyze authorized source artifacts in the isolated workspace.",
            ["ev-1"],
            []),
        new Dictionary<string, SpecialistTaskResult>());

    private static SpecialistAdapterExecutionEnvelope Envelope() => new(
        AdapterNetworkScope.None,
        AdapterWorkspaceScope.ReadWrite,
        AgentIsolationLevel.Container,
        MaximumSummaryUtf8Bytes: 8_192,
        MaximumEvidenceIds: 16,
        MaximumArtifactIds: 16);

    private sealed class AgentHandler(
        IReadOnlyList<string> evidenceIds,
        bool hostFilesystemMounted = false,
        AgentJobState finalState = AgentJobState.Succeeded) : HttpMessageHandler
    {
        public string? ObservedProjectRoot { get; private set; }
        public bool? PolicyHostFilesystemMounted { get; private set; }
        public bool? PolicyOutboundNetworkAllowed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                ObservedProjectRoot = root.GetProperty("projectRoot").GetString();
                var policy = root.GetProperty("policy");
                PolicyHostFilesystemMounted = policy.GetProperty("hostFilesystemMounted").GetBoolean();
                PolicyOutboundNetworkAllowed = policy.GetProperty("outboundNetworkAllowed").GetBoolean();
                return Json(new
                {
                    jobId = "job-1",
                    state = "Running",
                    acceptedAt = "2026-08-15T05:00:00+00:00"
                });
            }

            return Json(new
            {
                jobId = "job-1",
                state = finalState.ToString(),
                attestation = new
                {
                    isolation = "Container",
                    hostFilesystemMounted,
                    outboundNetworkAllowed = false,
                    projectRoot = ObservedProjectRoot
                },
                changedFiles = new[] { "analysis/result.json" },
                evidenceIds = evidenceIds.ToArray(),
                outputSummary = "sandbox analysis complete",
                artifactManifestSha256 = finalState == AgentJobState.Succeeded ? new string('a', 64) : null,
                observedAt = "2026-08-15T05:00:01+00:00"
            });
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "aevrix-agent-adapter-tests",
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
