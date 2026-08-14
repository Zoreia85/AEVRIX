using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aevrix.Remote.Capabilities;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class McpStreamableHttpClientTests
{
    private static readonly CapabilitySource ApprovedSource = new(
        RepositoryFullName: "example/mcp-server",
        SpdxLicense: "MIT",
        PinnedRevision: "0123456789abcdef0123456789abcdef01234567",
        ContentSha256: new string('b', 64));

    [TestMethod]
    public async Task ListTools_UsesModernHeadersAndRejectsMalformedHeaderAnnotations()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/mcp", request.RequestUri?.AbsolutePath);
            Assert.AreEqual("2026-07-28", Header(request, "MCP-Protocol-Version"));
            Assert.AreEqual("tools/list", Header(request, "Mcp-Method"));
            Assert.IsFalse(request.Headers.Contains("Mcp-Name"));
            CollectionAssert.AreEquivalent(
                new[] { "application/json", "text/event-stream" },
                request.Headers.Accept.Select(value => value.MediaType!).ToArray());

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Assert.AreEqual("2.0", body["jsonrpc"]!.GetValue<string>());
            Assert.AreEqual(1L, body["id"]!.GetValue<long>());
            Assert.AreEqual("tools/list", body["method"]!.GetValue<string>());
            var metadata = body["params"]!["_meta"]!.AsObject();
            Assert.AreEqual(
                "2026-07-28",
                metadata["io.modelcontextprotocol/protocolVersion"]!.GetValue<string>());

            return JsonResponse("""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": {
                    "nextCursor": "next-page",
                    "tools": [
                      {
                        "name": "lookup",
                        "description": "Safe lookup",
                        "inputSchema": {
                          "type": "object",
                          "properties": {
                            "region": { "type": "string", "x-mcp-header": "Region" }
                          }
                        }
                      },
                      {
                        "name": "invalid-number-header",
                        "inputSchema": {
                          "type": "object",
                          "properties": {
                            "score": { "type": "number", "x-mcp-header": "Score" }
                          }
                        }
                      },
                      {
                        "name": "duplicate-header",
                        "inputSchema": {
                          "type": "object",
                          "properties": {
                            "a": { "type": "string", "x-mcp-header": "Tenant" },
                            "b": { "type": "string", "x-mcp-header": "tenant" }
                          }
                        }
                      }
                    ]
                  }
                }
                """);
        });
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        var catalog = await client.ListToolsAsync();

        Assert.AreEqual(1, catalog.Tools.Count);
        Assert.AreEqual("lookup", catalog.Tools[0].Name);
        CollectionAssert.AreEqual(
            new[] { "duplicate-header", "invalid-number-header" },
            catalog.RejectedTools.ToArray());
        Assert.AreEqual("next-page", catalog.NextCursor);
    }

    [TestMethod]
    public async Task CallTool_MirrorsAnnotatedArgumentsAndBase64EncodesUnsafeHeaderValues()
    {
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual("tools/call", Header(request, "Mcp-Method"));
            Assert.AreEqual("buscar", Header(request, "Mcp-Name"));
            Assert.AreEqual("=?base64?IHBhZGRlZCA=?=", Header(request, "Mcp-Param-Text"));

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Assert.AreEqual("buscar", body["params"]!["name"]!.GetValue<string>());
            Assert.AreEqual(" padded ", body["params"]!["arguments"]!["text"]!.GetValue<string>());

            return JsonResponse("""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "result": { "content": [{ "type": "text", "text": "ok" }] }
                }
                """);
        });
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["x-mcp-header"] = "Text"
                }
            }
        };
        var tool = new McpToolDefinition("buscar", "Search", schema);

        var result = await client.CallToolAsync(
            tool,
            new JsonObject { ["text"] = " padded " });

        Assert.AreEqual("ok", result!["content"]![0]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task ReadResource_AcceptsRequestScopedSseNotificationsThenFinalResponse()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.AreEqual("resources/read", Header(request, "Mcp-Method"));
            Assert.AreEqual("file:///project/readme.md", Header(request, "Mcp-Name"));
            return Task.FromResult(SseResponse("""
                data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":0.5}}

                data: {"jsonrpc":"2.0","id":1,"result":{"contents":[]}}

                """));
        });
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        var result = await client.ReadResourceAsync("file:///project/readme.md");

        Assert.AreEqual(0, result!["contents"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task ResponseIdMismatchFailsClosed()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse("""
            { "jsonrpc": "2.0", "id": 99, "result": { "contents": [] } }
            """)));
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ReadResourceAsync("file:///project/readme.md"));
    }

    [TestMethod]
    public async Task ModernSseRejectsIndependentServerRequest()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(SseResponse("""
            data: {"jsonrpc":"2.0","id":1,"method":"sampling/createMessage","params":{}}

            """)));
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ReadResourceAsync("file:///project/readme.md"));
    }

    [TestMethod]
    public async Task ModernJsonRpcErrorIsSurfacedEvenOnHttp400()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(
            """
            {
              "jsonrpc": "2.0",
              "id": 1,
              "error": { "code": -32020, "message": "Header mismatch" }
            }
            """,
            HttpStatusCode.BadRequest)));
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.ReadResourceAsync("file:///project/readme.md"));

        Assert.AreEqual(-32020, exception.Code);
    }

    [TestMethod]
    public async Task NonSuccessHttpCannotSmuggleSuccessfulJsonRpcResult()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(
            """
            { "jsonrpc": "2.0", "id": 1, "result": { "contents": [] } }
            """,
            HttpStatusCode.InternalServerError)));
        using HttpClient httpClient = new(handler);
        var client = CreateClient(httpClient);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ReadResourceAsync("file:///project/readme.md"));
    }

    [TestMethod]
    public void ClientRejectsUnapprovedServerBeforeAnyNetworkActivity()
    {
        using HttpClient httpClient = new(new RecordingHandler((_, _) =>
            throw new AssertFailedException("Network must not be reached.")));
        var server = CreateServer(CapabilityApprovalState.Unreviewed);

        Assert.Throws<InvalidOperationException>(() =>
            new McpStreamableHttpClient(
                httpClient,
                server,
                new McpStreamableHttpClientOptions(TimeSpan.FromSeconds(10))));
    }

    [TestMethod]
    public void ClientRequiresExplicitCurrentProtocolRevision()
    {
        var options = new McpStreamableHttpClientOptions(
            TimeSpan.FromSeconds(10),
            ProtocolVersion: "2025-11-25");

        Assert.Throws<NotSupportedException>(options.Validate);
    }

    private static McpStreamableHttpClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            CreateServer(CapabilityApprovalState.Approved),
            new McpStreamableHttpClientOptions(TimeSpan.FromSeconds(10)));

    private static McpServerDescriptor CreateServer(CapabilityApprovalState approval) =>
        new(
            ServerId: "safe-mcp",
            Endpoint: new Uri("http://127.0.0.1:9000/mcp", UriKind.Absolute),
            Source: ApprovedSource,
            Approval: approval,
            ReadOnly: true,
            Capabilities: new[] { "repository-read" },
            RequiredSecretNames: Array.Empty<string>(),
            AllowedFilesystemRoots: Array.Empty<string>());

    private static string Header(HttpRequestMessage request, string name) =>
        request.Headers.GetValues(name).Single();

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _respond(request, cancellationToken);
    }
}
