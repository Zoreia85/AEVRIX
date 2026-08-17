using System.Net;
using System.Text;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class CapabilityHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 21, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task OllamaProbe_HealthyOnlyWhenConfiguredModelIsPresent()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/tags", request.RequestUri?.AbsolutePath);
            return JsonResponse("""
                {
                  "models": [
                    { "name": "qwen3:8b", "size": 123456, "digest": "sha256:example" }
                  ]
                }
                """);
        });
        using HttpClient client = new(handler);
        var provider = CreateOllamaProvider(client);
        var probe = new OllamaCapabilityHealthProbe(provider, "qwen3:8b", new FixedTimeProvider(Now));

        var observation = await probe.ProbeAsync();

        Assert.AreEqual("ollama", observation.ProviderId);
        Assert.AreEqual(CapabilityHealthState.Healthy, observation.Health);
        Assert.AreEqual(Now, observation.ObservedAt);
        Assert.AreEqual("runtime-and-model-ready", observation.Detail);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task OllamaProbe_MissingConfiguredModelFailsClosed()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "models": [
                { "name": "llama3.2:3b", "size": 1000, "digest": "sha256:other" }
              ]
            }
            """));
        using HttpClient client = new(handler);
        var probe = new OllamaCapabilityHealthProbe(
            CreateOllamaProvider(client),
            "qwen3:8b",
            new FixedTimeProvider(Now));

        var observation = await probe.ProbeAsync();

        Assert.AreEqual(CapabilityHealthState.Unavailable, observation.Health);
        Assert.AreEqual("configured-model-not-present", observation.Detail);
    }

    [TestMethod]
    public async Task HealthMonitor_ProbeFailureImmediatelyRemovesProviderFromRouting()
    {
        var broker = new CapabilityBroker();
        broker.Register(ModelProvider("ollama", Now.AddMinutes(-1)));
        var monitor = new CapabilityHealthMonitor(
            broker,
            new ICapabilityHealthProbe[] { new ThrowingProbe("ollama") },
            new FixedTimeProvider(Now));

        var observation = await monitor.ProbeProviderAsync("ollama");

        Assert.AreEqual(CapabilityHealthState.Unavailable, observation.Health);
        Assert.AreEqual(CapabilityHealthState.Unavailable, broker.Get("ollama").Health);
        Assert.AreEqual(0, broker.Rank("model-analysis", Now, TimeSpan.FromMinutes(5)).Count);
    }

    [TestMethod]
    public async Task HealthMonitor_IdentityMismatchFailsClosedAgainstRegisteredProvider()
    {
        var broker = new CapabilityBroker();
        broker.Register(ModelProvider("ollama", Now.AddMinutes(-1)));
        var monitor = new CapabilityHealthMonitor(
            broker,
            new ICapabilityHealthProbe[] { new MismatchedProbe("ollama", "other-provider", Now) },
            new FixedTimeProvider(Now));

        var observation = await monitor.ProbeProviderAsync("ollama");

        Assert.AreEqual("ollama", observation.ProviderId);
        Assert.AreEqual(CapabilityHealthState.Unavailable, observation.Health);
        StringAssert.StartsWith(observation.Detail, "probe-failed:");
        Assert.AreEqual(CapabilityHealthState.Unavailable, broker.Get("ollama").Health);
    }

    [TestMethod]
    public void HealthyProbeCannotSilentlyReleaseQuarantine()
    {
        var broker = new CapabilityBroker();
        broker.Register(ModelProvider("ollama", Now.AddMinutes(-1)));
        broker.SetQuarantined("ollama", true);

        var updated = broker.RecordHealthObservation(new CapabilityHealthObservation(
            "ollama",
            CapabilityHealthState.Healthy,
            50,
            Now,
            "runtime-ready"));

        Assert.AreEqual(CapabilityHealthState.Quarantined, updated.Health);
        Assert.AreEqual(0, broker.Rank("model-analysis", Now, TimeSpan.FromMinutes(5)).Count);
    }

    [TestMethod]
    public async Task ProbeAll_UsesBoundedRegistryAndReturnsDeterministicProviderOrder()
    {
        var broker = new CapabilityBroker();
        broker.Register(ModelProvider("provider-b", Now.AddMinutes(-1)));
        broker.Register(ModelProvider("provider-a", Now.AddMinutes(-1)));
        var monitor = new CapabilityHealthMonitor(
            broker,
            new ICapabilityHealthProbe[]
            {
                new StaticProbe("provider-b", CapabilityHealthState.Healthy, Now),
                new StaticProbe("provider-a", CapabilityHealthState.Degraded, Now)
            },
            new FixedTimeProvider(Now));

        var results = await monitor.ProbeAllAsync(maximumConcurrency: 2);

        CollectionAssert.AreEqual(
            new[] { "provider-a", "provider-b" },
            results.Select(result => result.ProviderId).ToArray());
        Assert.AreEqual(CapabilityHealthState.Degraded, broker.Get("provider-a").Health);
        Assert.AreEqual(CapabilityHealthState.Healthy, broker.Get("provider-b").Health);
    }

    private static OllamaModelProvider CreateOllamaProvider(HttpClient client) =>
        new(
            client,
            new OllamaRuntimeOptions(
                new Uri("http://127.0.0.1:11434", UriKind.Absolute),
                "qwen3:8b",
                TimeSpan.FromSeconds(30))
            {
                AllowedModels = new HashSet<string>(StringComparer.Ordinal) { "qwen3:8b" }
            });

    private static CapabilityProviderSnapshot ModelProvider(string providerId, DateTimeOffset observedAt) =>
        new(
            ProviderId: providerId,
            Capability: "model-analysis",
            Approval: CapabilityApprovalState.Approved,
            Health: CapabilityHealthState.Healthy,
            Enabled: true,
            QualityScore: 0.90,
            ReliabilityScore: 0.95,
            P95LatencyMilliseconds: 200,
            ConsecutiveFailures: 0,
            LastObservedAt: observedAt);

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

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ThrowingProbe : ICapabilityHealthProbe
    {
        public ThrowingProbe(string providerId)
        {
            ProviderId = providerId;
        }

        public string ProviderId { get; }

        public Task<CapabilityHealthObservation> ProbeAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("simulated provider outage");
    }

    private sealed class MismatchedProbe : ICapabilityHealthProbe
    {
        private readonly string _reportedProviderId;
        private readonly DateTimeOffset _observedAt;

        public MismatchedProbe(string providerId, string reportedProviderId, DateTimeOffset observedAt)
        {
            ProviderId = providerId;
            _reportedProviderId = reportedProviderId;
            _observedAt = observedAt;
        }

        public string ProviderId { get; }

        public Task<CapabilityHealthObservation> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapabilityHealthObservation(
                _reportedProviderId,
                CapabilityHealthState.Healthy,
                10,
                _observedAt));
    }

    private sealed class StaticProbe : ICapabilityHealthProbe
    {
        private readonly CapabilityHealthState _health;
        private readonly DateTimeOffset _observedAt;

        public StaticProbe(string providerId, CapabilityHealthState health, DateTimeOffset observedAt)
        {
            ProviderId = providerId;
            _health = health;
            _observedAt = observedAt;
        }

        public string ProviderId { get; }

        public Task<CapabilityHealthObservation> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CapabilityHealthObservation(
                ProviderId,
                _health,
                25,
                _observedAt));
    }
}
