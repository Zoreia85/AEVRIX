using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Aevrix.Remote.Capabilities;

public sealed record McpStreamableHttpClientOptions(
    TimeSpan RequestTimeout,
    int MaximumResponseBytes = 2_097_152,
    int MaximumSseEvents = 256,
    string ClientName = "aevrix",
    string ClientVersion = "0.1.0",
    string ProtocolVersion = "2026-07-28")
{
    public const string SupportedProtocolVersion = "2026-07-28";

    public McpStreamableHttpClientOptions Validate()
    {
        if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaximumResponseBytes is < 1_024 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }

        if (MaximumSseEvents is < 1 or > 2_048)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSseEvents));
        }

        if (!IsSafeClientToken(ClientName, 1, 80) || !IsSafeClientToken(ClientVersion, 1, 80))
        {
            throw new ArgumentException("MCP client identity is invalid.");
        }

        if (!string.Equals(ProtocolVersion, SupportedProtocolVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"This transport currently supports MCP protocol {SupportedProtocolVersion} only.");
        }

        return this;
    }

    private static bool IsSafeClientToken(string value, int minimum, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= minimum
        && value.Length <= maximum
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');
}

public sealed record McpToolDefinition(
    string Name,
    string? Description,
    JsonObject InputSchema);

public sealed record McpToolCatalogResult(
    IReadOnlyList<McpToolDefinition> Tools,
    IReadOnlyList<string> RejectedTools,
    string? NextCursor);

public sealed class McpProtocolException : Exception
{
    public McpProtocolException(int code, string message)
        : base($"MCP JSON-RPC error {code}: {message}")
    {
        Code = code;
        ProtocolMessage = message;
    }

    public int Code { get; }

    public string ProtocolMessage { get; }
}

/// <summary>
/// Minimal governed client for the MCP 2026-07-28 Streamable HTTP transport.
/// It intentionally does not implement legacy sessionful HTTP+SSE fallback.
/// Every request is an independent POST, and responses are accepted only as JSON
/// or request-scoped SSE with a matching JSON-RPC id.
/// </summary>
public sealed class McpStreamableHttpClient
{
    private const string JsonRpcVersion = "2.0";
    private const int MaximumHeaderValueCharacters = 4_096;

    private readonly HttpClient _httpClient;
    private readonly McpServerDescriptor _server;
    private readonly McpStreamableHttpClientOptions _options;
    private long _nextRequestId;

    public McpStreamableHttpClient(
        HttpClient httpClient,
        McpServerDescriptor server,
        McpStreamableHttpClientOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();

        if (!_server.CanConnect())
        {
            throw new InvalidOperationException(
                $"MCP server '{_server.ServerId}' is not approved for connection.");
        }
    }

    public string ServerId => _server.ServerId;

    public async Task<McpToolCatalogResult> ListToolsAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (cursor is { Length: > 2_048 })
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            parameters["cursor"] = cursor;
        }

        var result = await SendRequestAsync(
            "tools/list",
            requestName: null,
            parameters,
            mirroredHeaders: null,
            cancellationToken);

        if (result is not JsonObject resultObject || resultObject["tools"] is not JsonArray toolsArray)
        {
            throw new InvalidDataException("MCP tools/list result is malformed.");
        }

        var accepted = new List<McpToolDefinition>();
        var rejected = new List<string>();
        var index = 0;

        foreach (var item in toolsArray)
        {
            var fallbackName = $"<tool:{index}>";
            index++;

            try
            {
                if (item is not JsonObject tool)
                {
                    throw new InvalidDataException("Tool definition must be an object.");
                }

                var name = GetRequiredString(tool, "name", maximumLength: 512);
                fallbackName = name;
                var description = GetOptionalString(tool, "description", maximumLength: 32_768);
                if (tool["inputSchema"] is not JsonObject inputSchema)
                {
                    throw new InvalidDataException("Tool inputSchema must be an object.");
                }

                _ = InspectHeaderBindings(inputSchema);
                accepted.Add(new McpToolDefinition(
                    name,
                    description,
                    (JsonObject)inputSchema.DeepClone()));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                rejected.Add(fallbackName);
            }
        }

        var nextCursor = GetOptionalString(resultObject, "nextCursor", maximumLength: 2_048);
        return new McpToolCatalogResult(
            accepted.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(),
            rejected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            nextCursor);
    }

    public Task<JsonNode?> CallToolAsync(
        McpToolDefinition tool,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 512)
        {
            throw new ArgumentException("MCP tool name is invalid.", nameof(tool));
        }

        ArgumentNullException.ThrowIfNull(tool.InputSchema);
        var argumentObject = arguments is null
            ? new JsonObject()
            : (JsonObject)arguments.DeepClone();
        var mirroredHeaders = BuildMirroredToolHeaders(tool.InputSchema, argumentObject);
        var parameters = new JsonObject
        {
            ["name"] = tool.Name,
            ["arguments"] = argumentObject
        };

        return SendRequestAsync(
            "tools/call",
            tool.Name,
            parameters,
            mirroredHeaders,
            cancellationToken);
    }

    public Task<JsonNode?> ReadResourceAsync(
        string resourceUri,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestName(resourceUri, nameof(resourceUri));
        var parameters = new JsonObject
        {
            ["uri"] = resourceUri
        };

        return SendRequestAsync(
            "resources/read",
            resourceUri,
            parameters,
            mirroredHeaders: null,
            cancellationToken);
    }

    public Task<JsonNode?> GetPromptAsync(
        string promptName,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestName(promptName, nameof(promptName));
        var parameters = new JsonObject
        {
            ["name"] = promptName,
            ["arguments"] = arguments is null ? new JsonObject() : arguments.DeepClone()
        };

        return SendRequestAsync(
            "prompts/get",
            promptName,
            parameters,
            mirroredHeaders: null,
            cancellationToken);
    }

    private async Task<JsonNode?> SendRequestAsync(
        string method,
        string? requestName,
        JsonObject parameters,
        IReadOnlyDictionary<string, string>? mirroredHeaders,
        CancellationToken cancellationToken)
    {
        ValidateRpcMethod(method);
        ArgumentNullException.ThrowIfNull(parameters);

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var requestParameters = (JsonObject)parameters.DeepClone();
        requestParameters["_meta"] = CreateMetadata();

        var body = new JsonObject
        {
            ["jsonrpc"] = JsonRpcVersion,
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = requestParameters
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _server.Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _options.ProtocolVersion);
        request.Headers.TryAddWithoutValidation("Mcp-Method", method);

        if (requestName is not null)
        {
            ValidateRequestName(requestName, nameof(requestName));
            request.Headers.TryAddWithoutValidation("Mcp-Name", EncodeHeaderValue(requestName));
        }

        if (mirroredHeaders is not null)
        {
            foreach (var header in mirroredHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        JsonNode rpcResponse;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var json = await ReadBoundedUtf8Async(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                timeout.Token);
            rpcResponse = JsonNode.Parse(json)
                ?? throw new InvalidDataException("MCP response JSON is empty.");
        }
        else if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var sse = await ReadBoundedUtf8Async(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                timeout.Token);
            rpcResponse = ExtractFinalSseResponse(sse, requestId);
        }
        else
        {
            throw new InvalidDataException(
                $"MCP response content type '{mediaType ?? "<missing>"}' is not supported.");
        }

        try
        {
            var parsed = ParseJsonRpcResponse(rpcResponse, requestId);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"MCP server returned HTTP {(int)response.StatusCode} with a non-error JSON-RPC payload.",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            return parsed;
        }
        catch (McpProtocolException)
        {
            throw;
        }
    }

    private JsonObject CreateMetadata() => new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = _options.ProtocolVersion,
        ["io.modelcontextprotocol/clientInfo"] = new JsonObject
        {
            ["name"] = _options.ClientName,
            ["version"] = _options.ClientVersion
        },
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
    };

    private JsonNode ExtractFinalSseResponse(string sse, long expectedRequestId)
    {
        using var reader = new StringReader(sse);
        var dataLines = new List<string>();
        JsonNode? finalResponse = null;
        var eventCount = 0;

        void ProcessEvent()
        {
            if (dataLines.Count == 0)
            {
                return;
            }

            eventCount++;
            if (eventCount > _options.MaximumSseEvents)
            {
                throw new InvalidDataException("MCP SSE response exceeded the configured event limit.");
            }

            var payload = string.Join("\n", dataLines);
            dataLines.Clear();
            var node = JsonNode.Parse(payload)
                ?? throw new InvalidDataException("MCP SSE event contained empty JSON.");
            if (node is not JsonObject obj)
            {
                throw new InvalidDataException("MCP SSE event must contain a JSON-RPC object.");
            }

            var hasId = obj.TryGetPropertyValue("id", out var idNode) && idNode is not null;
            var hasMethod = obj.TryGetPropertyValue("method", out var methodNode) && methodNode is not null;
            if (!hasId)
            {
                if (!hasMethod)
                {
                    throw new InvalidDataException("MCP SSE event is neither a notification nor a response.");
                }

                return;
            }

            if (hasMethod)
            {
                throw new InvalidDataException("MCP 2026-07-28 server requests are not valid on Streamable HTTP SSE responses.");
            }

            var id = GetResponseId(obj);
            if (id != expectedRequestId)
            {
                throw new InvalidDataException("MCP SSE response id does not match the originating request.");
            }

            if (finalResponse is not null)
            {
                throw new InvalidDataException("MCP SSE stream returned multiple final responses.");
            }

            finalResponse = obj.DeepClone();
        }

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                ProcessEvent();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                dataLines.Add(value);
            }
        }

        ProcessEvent();
        return finalResponse
            ?? throw new InvalidDataException("MCP SSE stream ended without a final JSON-RPC response.");
    }

    private static JsonNode? ParseJsonRpcResponse(JsonNode node, long expectedRequestId)
    {
        if (node is not JsonObject response)
        {
            throw new InvalidDataException("MCP JSON-RPC response must be an object.");
        }

        if (!string.Equals(GetRequiredString(response, "jsonrpc", 8), JsonRpcVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("MCP JSON-RPC version is invalid.");
        }

        if (response.TryGetPropertyValue("method", out var methodNode) && methodNode is not null)
        {
            throw new InvalidDataException("MCP server returned a request where a response was required.");
        }

        if (GetResponseId(response) != expectedRequestId)
        {
            throw new InvalidDataException("MCP JSON-RPC response id does not match the originating request.");
        }

        var hasResult = response.TryGetPropertyValue("result", out var resultNode);
        var hasError = response.TryGetPropertyValue("error", out var errorNode) && errorNode is not null;
        if (hasResult == hasError)
        {
            throw new InvalidDataException("MCP JSON-RPC response must contain exactly one of result or error.");
        }

        if (hasError)
        {
            if (errorNode is not JsonObject error)
            {
                throw new InvalidDataException("MCP JSON-RPC error is malformed.");
            }

            var code = GetRequiredInt32(error, "code");
            var message = GetRequiredString(error, "message", 8_192);
            throw new McpProtocolException(code, message);
        }

        return resultNode?.DeepClone();
    }

    private static long GetResponseId(JsonObject response)
    {
        if (response["id"] is not JsonValue value || !value.TryGetValue<long>(out var id))
        {
            throw new InvalidDataException("MCP JSON-RPC response id must be an integer.");
        }

        return id;
    }

    private IReadOnlyDictionary<string, string> BuildMirroredToolHeaders(
        JsonObject inputSchema,
        JsonObject arguments)
    {
        var bindings = InspectHeaderBindings(inputSchema);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings)
        {
            var node = ResolveArgument(arguments, binding.Path);
            if (node is null)
            {
                continue;
            }

            var raw = ConvertPrimitiveHeaderValue(node, binding.PrimitiveType);
            if (raw.Length > MaximumHeaderValueCharacters)
            {
                throw new InvalidDataException("MCP mirrored header value exceeds the configured limit.");
            }

            headers[$"Mcp-Param-{binding.HeaderName}"] = EncodeHeaderValue(raw);
        }

        return headers;
    }

    private static IReadOnlyList<McpHeaderBinding> InspectHeaderBindings(JsonObject inputSchema)
    {
        ArgumentNullException.ThrowIfNull(inputSchema);
        var bindings = new List<McpHeaderBinding>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectHeaderBindings(inputSchema, Array.Empty<string>(), bindings, names);
        return bindings;
    }

    private static void CollectHeaderBindings(
        JsonObject schema,
        IReadOnlyList<string> prefix,
        ICollection<McpHeaderBinding> bindings,
        ISet<string> names)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var property in properties)
        {
            if (property.Value is not JsonObject propertySchema)
            {
                continue;
            }

            var path = prefix.Concat(new[] { property.Key }).ToArray();
            if (propertySchema.TryGetPropertyValue("x-mcp-header", out var headerNode) && headerNode is not null)
            {
                if (headerNode is not JsonValue headerValue
                    || !headerValue.TryGetValue<string>(out var headerName)
                    || string.IsNullOrWhiteSpace(headerName)
                    || !IsHttpFieldNameToken(headerName))
                {
                    throw new InvalidDataException("MCP x-mcp-header annotation is invalid.");
                }

                if (!names.Add(headerName))
                {
                    throw new InvalidDataException("MCP x-mcp-header names must be case-insensitively unique.");
                }

                var primitiveType = GetRequiredString(propertySchema, "type", 16);
                if (primitiveType is not ("string" or "integer" or "boolean"))
                {
                    throw new InvalidDataException("MCP x-mcp-header may only annotate string, integer, or boolean properties.");
                }

                bindings.Add(new McpHeaderBinding(path, headerName, primitiveType));
            }

            CollectHeaderBindings(propertySchema, path, bindings, names);
        }
    }

    private static JsonNode? ResolveArgument(JsonObject arguments, IReadOnlyList<string> path)
    {
        JsonNode? current = arguments;
        foreach (var segment in path)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string ConvertPrimitiveHeaderValue(JsonNode node, string primitiveType)
    {
        if (node is not JsonValue value)
        {
            throw new InvalidDataException("MCP mirrored header argument must be a primitive JSON value.");
        }

        return primitiveType switch
        {
            "string" when value.TryGetValue<string>(out var text) => text,
            "boolean" when value.TryGetValue<bool>(out var boolean) => boolean ? "true" : "false",
            "integer" when value.TryGetValue<long>(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("MCP mirrored header argument type does not match inputSchema.")
        };
    }

    internal static string EncodeHeaderValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumHeaderValueCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var sentinel = value.StartsWith("=?base64?", StringComparison.Ordinal)
            && value.EndsWith("?=", StringComparison.Ordinal);
        var leadingOrTrailingWhitespace = value.Length > 0
            && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        var unsafeCharacter = value.Any(ch =>
            ch > 0x7e
            || (ch < 0x20 && ch != '\t'));

        if (!sentinel && !leadingOrTrailingWhitespace && !unsafeCharacter)
        {
            return value;
        }

        return $"=?base64?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
    }

    private static bool IsHttpFieldNameToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 80)
        {
            return false;
        }

        const string punctuation = "!#$%&'*+-.^_`|~";
        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || punctuation.Contains(ch));
    }

    private static void ValidateRpcMethod(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 160
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '/' or '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException("MCP JSON-RPC method is invalid.", nameof(value));
        }
    }

    private static void ValidateRequestName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumHeaderValueCharacters)
        {
            throw new ArgumentException("MCP request name or URI is invalid.", parameterName);
        }
    }

    private async Task<string> ReadBoundedUtf8Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await using var owned = stream;
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];

        while (true)
        {
            var read = await owned.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > _options.MaximumResponseBytes)
            {
                throw new InvalidDataException("MCP response exceeded the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string GetRequiredString(JsonObject obj, string propertyName, int maximumLength)
    {
        if (obj[propertyName] is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maximumLength)
        {
            throw new InvalidDataException($"MCP property '{propertyName}' is missing or invalid.");
        }

        return text;
    }

    private static string? GetOptionalString(JsonObject obj, string propertyName, int maximumLength)
    {
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || text.Length > maximumLength)
        {
            throw new InvalidDataException($"MCP property '{propertyName}' is invalid.");
        }

        return text;
    }

    private static int GetRequiredInt32(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is not JsonValue value || !value.TryGetValue<int>(out var number))
        {
            throw new InvalidDataException($"MCP property '{propertyName}' is missing or invalid.");
        }

        return number;
    }

    private sealed record McpHeaderBinding(
        IReadOnlyList<string> Path,
        string HeaderName,
        string PrimitiveType);
}
