using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class SandboxAgentBackendClientTests
{
    private static readonly CapabilitySource ApprovedSource = new(
        RepositoryFullName: "example/sandbox-agent",
        SpdxLicense: "MIT",
        PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
        ContentSha256: new string('d', 64));

    [TestMethod]
    public async Task Submit_TransmitsLeastPrivilegePolicyAndBoundedProjectRoot()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/agent/v1/jobs", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("1", request.Headers.GetValues("X-AEVRIX-Agent-Contract").Single());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Assert.AreEqual("WORK-001", body["workId"]!.GetValue<string>());
            Assert.AreEqual("/projects/aevrix/module", body["projectRoot"]!.GetValue<string>());
            Assert.AreEqual("Container", body["policy"]!["isolation"]!.GetValue<string>());
            Assert.IsFalse(body["policy"]!["hostFilesystemMounted"]!.GetValue<bool>());
            Assert.IsFalse(body["policy"]!["outboundNetworkAllowed"]!.GetValue<bool>());
            Assert.AreEqual(900, body["policy"]!["maximumRuntimeSeconds"]!.GetValue<int>());

            return JsonResponse("""
                {
                  "jobId":"JOB-001",
                  "state":"Queued",
                  "acceptedAt":"2026-08-14T21:20:00Z"
                }
                """);
        });
        using HttpClient http = new(handler);
        var client = CreateClient(http);

        var receipt = await client.SubmitAsync(new AgentWorkRequest(
            "WORK-001",
            "Inspect the governed project and propose a bounded patch.",
            "/projects/aevrix/module",
            new[] { "EV-001" }));

        Assert.AreEqual("JOB-001", receipt.JobId);
        Assert.AreEqual(AgentJobState.Queued, receipt.State);
    }

    [TestMethod]
    public async Task Submit_RejectsProjectOutsideAllowlistBeforeNetwork()
    {
        var handler = new RecordingHandler((_, _) =>
            throw new AssertFailedException("Network must not be reached."));
        using HttpClient http = new(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitAsync(new AgentWorkRequest(
            "WORK-002",
            "Do work",
            "/other/project",
            Array.Empty<string>())));
    }

    [TestMethod]
    public void WorkRequestRejectsTraversalRoot()
    {
        var work = new AgentWorkRequest(
            "WORK-003",
            "Do work",
            "/projects/aevrix/../secrets",
            Array.Empty<string>());

        Assert.Throws<ArgumentException>(work.Validate);
    }

    [TestMethod]
    public async Task ResultRequiresMatchingIsolationAttestationAndManifestHash()
    {
        using HttpClient http = new(new StaticHandler(JsonResponse("""
            {
              "jobId":"JOB-004",
              "state":"Succeeded",
              "attestation":{
                "isolation":"Container",
                "hostFilesystemMounted":false,
                "outboundNetworkAllowed":false,
                "projectRoot":"/projects/aevrix/module"
              },
              "changedFiles":["src/b.cs","src/a.cs","src/a.cs"],
              "evidenceIds":["EV-101"],
              "outputSummary":"Patch prepared in sandbox.",
              "artifactManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "observedAt":"2026-08-14T21:21:00Z"
            }
            """)));
        var client = CreateClient(http);

        var result = await client.GetResultAsync("JOB-004", "/projects/aevrix/module");

        Assert.AreEqual(AgentJobState.Succeeded, result.State);
        CollectionAssert.AreEqual(new[] { "src/a.cs", "src/b.cs" }, result.ChangedFiles.ToArray());
        Assert.IsFalse(result.Attestation.HostFilesystemMounted);
        Assert.AreEqual(AgentIsolationLevel.Container, result.Attestation.Isolation);
    }

    [TestMethod]
    public async Task ResultRejectsBackendClaimingHostFilesystemMount()
    {
        using HttpClient http = new(new StaticHandler(JsonResponse("""
            {
              "jobId":"JOB-005",
              "state":"Succeeded",
              "attestation":{
                "isolation":"Container",
                "hostFilesystemMounted":true,
                "outboundNetworkAllowed":false,
                "projectRoot":"/projects/aevrix/module"
              },
              "changedFiles":[],
              "evidenceIds":[],
              "artifactManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "observedAt":"2026-08-14T21:22:00Z"
            }
            """)));
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetResultAsync("JOB-005", "/projects/aevrix/module"));
    }

    [TestMethod]
    public async Task ResultRejectsChangedFileTraversal()
    {
        using HttpClient http = new(new StaticHandler(JsonResponse("""
            {
              "jobId":"JOB-006",
              "state":"Succeeded",
              "attestation":{
                "isolation":"Container",
                "hostFilesystemMounted":false,
                "outboundNetworkAllowed":false,
                "projectRoot":"/projects/aevrix/module"
              },
              "changedFiles":["../outside.txt"],
              "evidenceIds":[],
              "artifactManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "observedAt":"2026-08-14T21:23:00Z"
            }
            """)));
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetResultAsync("JOB-006", "/projects/aevrix/module"));
    }

    [TestMethod]
    public async Task SuccessfulResultWithoutManifestHashFailsClosed()
    {
        using HttpClient http = new(new StaticHandler(JsonResponse("""
            {
              "jobId":"JOB-007",
              "state":"Succeeded",
              "attestation":{
                "isolation":"Container",
                "hostFilesystemMounted":false,
                "outboundNetworkAllowed":false,
                "projectRoot":"/projects/aevrix/module"
              },
              "changedFiles":[],
              "evidenceIds":[],
              "observedAt":"2026-08-14T21:24:00Z"
            }
            """)));
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetResultAsync("JOB-007", "/projects/aevrix/module"));
    }

    [TestMethod]
    public void ClientRejectsLocalProcessBackendEvenWhenApproved()
    {
        using HttpClient http = new(new RecordingHandler((_, _) =>
            throw new AssertFailedException("Network must not be reached.")));
        var backend = CreateBackend() with { Isolation = AgentIsolationLevel.LocalProcess };

        Assert.Throws<InvalidOperationException>(() => new SandboxAgentBackendClient(
            http,
            backend,
            new SandboxAgentBackendClientOptions(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(15))));
    }

    private static SandboxAgentBackendClient CreateClient(HttpClient http) =>
        new(
            http,
            CreateBackend(),
            new SandboxAgentBackendClientOptions(
                RequestTimeout: TimeSpan.FromSeconds(10),
                MaximumJobRuntime: TimeSpan.FromMinutes(15)));

    private static AgentBackendDescriptor CreateBackend() => new(
        BackendId: "sandbox-agent",
        Endpoint: new Uri("http://127.0.0.1:8000/agent", UriKind.Absolute),
        Source: ApprovedSource,
        Isolation: AgentIsolationLevel.Container,
        Approval: CapabilityApprovalState.Approved,
        AllowedProjectRoots: new[] { "/projects/aevrix" },
        HostFilesystemMounted: false,
        OutboundNetworkAllowed: false);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_response);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _respond(request, cancellationToken);
    }
}
