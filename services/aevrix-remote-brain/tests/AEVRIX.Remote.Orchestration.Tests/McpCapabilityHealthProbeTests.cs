using System.Net;
using System.Text;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class McpCapabilityHealthProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 21, 10, 0, TimeSpan.Zero);
    private static readonly CapabilitySource ApprovedSource = new(
        RepositoryFullName: "example/mcp-health",
        SpdxLicense: "MIT",
        PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
        ContentSha256: new string('c', 64));

    [TestMethod]
    public async Task ValidCatalogIsHealthyAndUpdatesBroker()
    {
        using HttpClient http = new(new StaticHandler(JsonResponse("""
            {
              "jsonrpc":"2.0",
              "id":1,
              "result":{
                "tools":[{
                  "name":"lookup",
                  "inputSchema":{"type":"object","properties":{}}
                }]
              }
            }
            """)));
        var probe = new McpCapabilityHealthProbe(CreateClient(http), new FixedTimeProvider(Now));
        var broker = CreateBroker();
        var monitor = new CapabilityHealthMonitor(broker, new ICapabilityHealthProbe[] { probe }, new FixedTimeProvider(Now));

        var observation = await monitor.ProbeProviderAsync("safe-mcp");

        Assert.AreEqual(CapabilityHealthState.Healthy, observation.Health);
        Assert.AreEqual("mcp-ready:tools=1", observation.Detail);
        Assert.AreEqual(CapabilityHealthState.Healthy, broker.Get("safe-mcp").Health);
    }

    [TestMethod]
    public async Task RejectedToolSchemaDegradesReachableServer()
    {
        using HttpClient http = new(new StaticHandler(JsonResponse("""
            {
              "jsonrpc":"2.0",
              "id":1,
              "result":{
                "tools":[{
                  "name":"unsafe-schema",
                  "inputSchema":{
                    "type":"object",
                    "properties":{
                      "score":{"type":"number","x-mcp-header":"Score"}
                    }
                  }
                }]
              }
            }
            """)));
        var probe = new McpCapabilityHealthProbe(CreateClient(http), new FixedTimeProvider(Now));

        var observation = await probe.ProbeAsync();

        Assert.AreEqual(CapabilityHealthState.Degraded, observation.Health);
        Assert.AreEqual("mcp-schema-degraded:accepted=0;rejected=1", observation.Detail);
    }

    [TestMethod]
    public async Task TransportFailureMarksServerUnavailable()
    {
        using HttpClient http = new(new ThrowingHandler());
        var probe = new McpCapabilityHealthProbe(CreateClient(http), new FixedTimeProvider(Now));

        var observation = await probe.ProbeAsync();

        Assert.AreEqual(CapabilityHealthState.Unavailable, observation.Health);
        StringAssert.StartsWith(observation.Detail, "mcp-probe-failed:");
    }

    private static McpStreamableHttpClient CreateClient(HttpClient http) =>
        new(
            http,
            new McpServerDescriptor(
                ServerId: "safe-mcp",
                Endpoint: new Uri("http://127.0.0.1:9000/mcp", UriKind.Absolute),
                Source: ApprovedSource,
                Approval: CapabilityApprovalState.Approved,
                ReadOnly: true,
                Capabilities: new[] { "repository-read" },
                RequiredSecretNames: Array.Empty<string>(),
                AllowedFilesystemRoots: Array.Empty<string>()),
            new McpStreamableHttpClientOptions(TimeSpan.FromSeconds(10)));

    private static CapabilityBroker CreateBroker()
    {
        var broker = new CapabilityBroker();
        broker.Register(new CapabilityProviderSnapshot(
            ProviderId: "safe-mcp",
            Capability: "mcp-server",
            Approval: CapabilityApprovalState.Approved,
            Health: CapabilityHealthState.Degraded,
            Enabled: true,
            QualityScore: 0.90,
            ReliabilityScore: 0.90,
            P95LatencyMilliseconds: 200,
            ConsecutiveFailures: 1,
            LastObservedAt: Now.AddMinutes(-1)));
        return broker;
    }

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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated outage");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
