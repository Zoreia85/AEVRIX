using System.Net;
using System.Text;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class OllamaLegacyTrustBoundaryTests
{
    [TestMethod]
    public void RemoteEndpointFlagCannotBypassLocalBoundary()
    {
        try
        {
            _ = new OllamaRuntimeOptions(
                new Uri("https://example.com:11434", UriKind.Absolute),
                "qwen3:8b",
                TimeSpan.FromSeconds(30),
                AllowRemoteEndpoint: true)
            {
                AllowedModels = Allowlist()
            }.Validate();
            Assert.Fail("Expected remote Ollama endpoint rejection.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public async Task ModelCannotSelfPromoteTrustOrInventEvidence()
    {
        var handler = new StaticHandler(JsonResponse("""
            {
              "model": "qwen3:8b",
              "message": {
                "content": "{\"statement\":\"candidate finding\",\"confidence\":0.99,\"risk\":\"Low\",\"evidenceIds\":[\"invented-evidence\"],\"assumptions\":[],\"openQuestions\":[]}"
              }
            }
            """));
        using HttpClient client = new(handler);
        var provider = new OllamaModelProvider(
            client,
            new OllamaRuntimeOptions(
                new Uri("http://127.0.0.1:11434", UriKind.Absolute),
                "qwen3:8b",
                TimeSpan.FromSeconds(30))
            {
                AllowedModels = Allowlist()
            });

        var task = new AnalysisTask(
            "task-legacy-ollama-001",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "target-01",
            "Inspect governed evidence.",
            new[] { "ev-001" },
            new Dictionary<string, string>());

        var candidate = await provider.AnalyzeAsync(task);

        Assert.AreEqual(ModelRiskLevel.High, candidate.Risk);
        Assert.IsTrue(Math.Abs(candidate.Confidence - 0.55) < 0.0001);
        CollectionAssert.AreEqual(new[] { "ev-001" }, candidate.EvidenceIds.ToArray());
        Assert.IsFalse(candidate.EvidenceIds.Contains("invented-evidence"));
        Assert.IsTrue(candidate.Assumptions.Any(assumption =>
            assumption.Contains("cannot self-promote", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlySet<string> Allowlist() =>
        new HashSet<string>(StringComparer.Ordinal) { "qwen3:8b" };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response);
        }
    }
}
