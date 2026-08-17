using System.Net;
using System.Text;
using Aevrix.Remote.Capabilities;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class CapabilityFabricTests
{
    private static readonly CapabilitySource ApprovedSource = new(
        RepositoryFullName: "example/tool",
        SpdxLicense: "MIT",
        PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
        ContentSha256: new string('a', 64));

    [TestMethod]
    public void OllamaRuntime_DefaultLocalOnlyRejectsRemoteEndpoint()
    {
        var options = new OllamaRuntimeOptions(
            new Uri("https://models.example.com", UriKind.Absolute),
            "qwen3:8b",
            TimeSpan.FromSeconds(30));

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [TestMethod]
    public async Task OllamaProvider_ParsesGovernedJsonFromChatEndpoint()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/chat", request.RequestUri?.AbsolutePath);
            const string response = """
                {
                  "model": "qwen3:8b",
                  "message": {
                    "content": "{\"statement\":\"Observed transition.\",\"confidence\":0.91,\"risk\":\"Low\",\"evidenceIds\":[\"EV-001\"],\"assumptions\":[],\"openQuestions\":[]}"
                  }
                }
                """;
            return JsonResponse(response);
        });
        using HttpClient client = new(handler);
        var provider = new OllamaModelProvider(
            client,
            new OllamaRuntimeOptions(
                new Uri("http://127.0.0.1:11434", UriKind.Absolute),
                "qwen3:8b",
                TimeSpan.FromSeconds(30))
            {
                AllowedModels = new HashSet<string>(StringComparer.Ordinal) { "qwen3:8b" }
            });

        var candidate = await provider.AnalyzeAsync(CreateTask());

        Assert.AreEqual("ollama", candidate.ProviderId);
        Assert.AreEqual("qwen3:8b", candidate.ProviderModelVersion);
        Assert.AreEqual(0.55, candidate.Confidence, 0.0001);
        Assert.AreEqual(ModelRiskLevel.High, candidate.Risk);
        CollectionAssert.AreEqual(new[] { "EV-001" }, candidate.EvidenceIds.ToArray());
        Assert.IsTrue(candidate.Assumptions.Any(assumption =>
            assumption.Contains("cannot self-promote", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public void McpRegistry_DeniedAutomaticExecutionFailsClosed()
    {
        var registry = new CapabilityRegistry();
        var descriptor = new McpServerDescriptor(
            ServerId: "unsafe-mcp",
            Endpoint: new Uri("http://127.0.0.1:9000/mcp", UriKind.Absolute),
            Source: ApprovedSource,
            Approval: CapabilityApprovalState.Approved,
            ReadOnly: true,
            Capabilities: new[] { "repository-read", "automatic-server-execution" },
            RequiredSecretNames: Array.Empty<string>(),
            AllowedFilesystemRoots: Array.Empty<string>());

        Assert.Throws<InvalidOperationException>(() => registry.RegisterMcp(descriptor));
        Assert.AreEqual(0, registry.ConnectableMcpServers().Count);
    }

    [TestMethod]
    public void McpRegistry_ApprovedReviewedServerCanConnect()
    {
        var registry = new CapabilityRegistry();
        var descriptor = new McpServerDescriptor(
            ServerId: "safe-mcp",
            Endpoint: new Uri("http://localhost:9000/mcp", UriKind.Absolute),
            Source: ApprovedSource,
            Approval: CapabilityApprovalState.Approved,
            ReadOnly: true,
            Capabilities: new[] { "repository-read" },
            RequiredSecretNames: new[] { "GITHUB_TOKEN" },
            AllowedFilesystemRoots: Array.Empty<string>());
        registry.RegisterMcp(descriptor);

        Assert.AreEqual(1, registry.ConnectableMcpServers().Count);
    }

    [TestMethod]
    public void AgentBackend_RequiresSandboxAndNoHostFilesystem()
    {
        var registry = new CapabilityRegistry();
        registry.RegisterAgentBackend(new AgentBackendDescriptor(
            BackendId: "container-agent",
            Endpoint: new Uri("http://127.0.0.1:8000", UriKind.Absolute),
            Source: ApprovedSource,
            Isolation: AgentIsolationLevel.Container,
            Approval: CapabilityApprovalState.Approved,
            AllowedProjectRoots: new[] { "/projects/aevrix" },
            HostFilesystemMounted: false,
            OutboundNetworkAllowed: false));

        Assert.AreEqual(1, registry.RunnableAgentBackends().Count);
    }

    [TestMethod]
    public void AgentBackend_LocalProcessIsNotRunnableByDefault()
    {
        var registry = new CapabilityRegistry();
        registry.RegisterAgentBackend(new AgentBackendDescriptor(
            BackendId: "local-agent",
            Endpoint: new Uri("http://localhost:8000", UriKind.Absolute),
            Source: ApprovedSource,
            Isolation: AgentIsolationLevel.LocalProcess,
            Approval: CapabilityApprovalState.Approved,
            AllowedProjectRoots: new[] { "/projects/aevrix" },
            HostFilesystemMounted: false,
            OutboundNetworkAllowed: false));

        Assert.AreEqual(0, registry.RunnableAgentBackends().Count);
    }

    private static AnalysisTask CreateTask() => new(
        TaskId: "TASK-0001",
        ProjectId: Guid.Parse("2d3fa6f3-984f-4db9-b2ca-3d86817acb44"),
        TargetId: "target:web",
        Objective: "Determine an observed state transition from governed evidence.",
        EvidenceIds: new[] { "EV-001" },
        Context: new Dictionary<string, string>
        {
            ["observation"] = "EV-001 records the transition."
        });

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(_respond(request));
        }
    }
}
