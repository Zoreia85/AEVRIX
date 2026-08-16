using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class OllamaLocalModelProviderTests
{
    [TestMethod]
    public void Rejects_non_loopback_endpoint()
    {
        using var transport = new HttpMessageInvoker(new FakeHandler(_ => Json("{}")));
        try
        {
            _ = new OllamaLocalModelProvider(transport, Policy(), new Uri("https://example.com:11434/"));
            Assert.Fail("Expected non-loopback endpoint rejection.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public async Task Rejects_non_allowlisted_model_before_transport()
    {
        var handler = new FakeHandler(_ => throw new AssertFailedException("transport called"));
        using var transport = new HttpMessageInvoker(handler);
        using var provider = new OllamaLocalModelProvider(
            transport, Policy(), new Uri("http://127.0.0.1:11434/"));

        try
        {
            await provider.AnalyzeAsync(BuildTask("unapproved:model"));
            Assert.Fail("Expected allowlist rejection.");
        }
        catch (InvalidOperationException)
        {
        }

        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Generate_output_remains_high_risk_candidate()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/generate", request.RequestUri!.AbsolutePath);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            StringAssert.Contains(body, "\"model\":\"qwen3:8b\"");
            StringAssert.Contains(body, "\"stream\":false");
            return Json("{\"model\":\"qwen3:8b\",\"response\":\"candidate finding\",\"done\":true}");
        });
        using var transport = new HttpMessageInvoker(handler);
        using var provider = new OllamaLocalModelProvider(
            transport, Policy(), new Uri("http://localhost:11434/"));

        var candidate = await provider.AnalyzeAsync(BuildTask("qwen3:8b"));

        Assert.AreEqual("ollama-local-rest", candidate.ProviderId);
        Assert.AreEqual("candidate finding", candidate.Statement);
        Assert.AreEqual(ModelRiskLevel.High, candidate.Risk);
        Assert.IsTrue(candidate.Confidence < 0.78);
        CollectionAssert.AreEqual(new[] { "ev-001" }, candidate.EvidenceIds.ToArray());
        Assert.AreEqual(1, handler.Calls);
    }

    private static LocalModelProviderPolicy Policy() =>
        new(new HashSet<string>(StringComparer.Ordinal) { "qwen3:8b" },
            RequestTimeout: TimeSpan.FromSeconds(5));

    private static AnalysisTask BuildTask(string model) =>
        new(
            "task-ollama-001",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "target-01",
            "Inspect governed evidence.",
            new[] { "ev-001" },
            new Dictionary<string, string> { [OllamaLocalModelProvider.ModelContextKey] = model });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(_response(request));
        }
    }
}
