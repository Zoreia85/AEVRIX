using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Capabilities;

public sealed record OllamaRuntimeOptions(
    Uri BaseAddress,
    string Model,
    TimeSpan RequestTimeout,
    bool AllowRemoteEndpoint = false)
{
    public OllamaRuntimeOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(BaseAddress);

        if (!BaseAddress.IsAbsoluteUri
            || !string.IsNullOrEmpty(BaseAddress.UserInfo)
            || !string.IsNullOrEmpty(BaseAddress.Query)
            || !string.IsNullOrEmpty(BaseAddress.Fragment))
        {
            throw new ArgumentException("Ollama base address must be an absolute URI without credentials, query, or fragment.", nameof(BaseAddress));
        }

        var isHttp = string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !isHttps)
        {
            throw new ArgumentException("Ollama base address must use HTTP or HTTPS.", nameof(BaseAddress));
        }

        if (!AllowRemoteEndpoint && !BaseAddress.IsLoopback)
        {
            throw new InvalidOperationException("Remote Ollama endpoints are disabled by default.");
        }

        if (!IsSafeModelName(Model))
        {
            throw new ArgumentException("Ollama model name is invalid.", nameof(Model));
        }

        if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        return this;
    }

    private static bool IsSafeModelName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 160
        && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '/');
}

public sealed record OllamaModelInfo(string Name, long? SizeBytes, string? Digest);

public sealed class OllamaModelProvider : IAevrixModelProvider
{
    private const int MaximumResponseBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OllamaRuntimeOptions _options;

    public OllamaModelProvider(HttpClient httpClient, OllamaRuntimeOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
    }

    public string ProviderId => "ollama";

    public async Task<ModelAnalysisCandidate> AnalyzeAsync(
        AnalysisTask task,
        CancellationToken cancellationToken = default)
    {
        task.Validate();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_options.BaseAddress, "/api/chat"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(CreateRequest(task), JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            throw new HttpRequestException(
                $"Ollama returned HTTP {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
        }

        var envelopeJson = await ReadBoundedUtf8Async(
            await response.Content.ReadAsStreamAsync(timeout.Token),
            MaximumResponseBytes,
            timeout.Token);
        var envelope = JsonSerializer.Deserialize<OllamaChatEnvelope>(envelopeJson, JsonOptions)
            ?? throw new InvalidDataException("Ollama response envelope is missing.");

        if (envelope.Message is null || string.IsNullOrWhiteSpace(envelope.Message.Content))
        {
            throw new InvalidDataException("Ollama response did not contain assistant content.");
        }

        var payload = envelope.Message.Content.Trim();
        if (!payload.StartsWith('{') || !payload.EndsWith('}'))
        {
            throw new InvalidDataException("Ollama analysis must be a single JSON object without markdown wrappers.");
        }

        var analysis = JsonSerializer.Deserialize<OllamaAnalysisPayload>(payload, JsonOptions)
            ?? throw new InvalidDataException("Ollama analysis payload is missing.");

        if (!Enum.TryParse<ModelRiskLevel>(analysis.Risk, ignoreCase: true, out var risk))
        {
            throw new InvalidDataException("Ollama analysis risk is invalid.");
        }

        var candidate = new ModelAnalysisCandidate(
            ProviderId,
            string.IsNullOrWhiteSpace(envelope.Model) ? _options.Model : envelope.Model.Trim(),
            analysis.Statement?.Trim() ?? string.Empty,
            analysis.Confidence,
            risk,
            analysis.EvidenceIds ?? Array.Empty<string>(),
            analysis.Assumptions ?? Array.Empty<string>(),
            analysis.OpenQuestions ?? Array.Empty<string>());

        return candidate.Validate();
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_options.BaseAddress, "/api/tags"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();

        var json = await ReadBoundedUtf8Async(
            await response.Content.ReadAsStreamAsync(timeout.Token),
            MaximumResponseBytes,
            timeout.Token);
        var envelope = JsonSerializer.Deserialize<OllamaTagsEnvelope>(json, JsonOptions);

        return envelope?.Models?
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(model => new OllamaModelInfo(model.Name!.Trim(), model.Size, model.Digest))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<OllamaModelInfo>();
    }

    private OllamaChatRequest CreateRequest(AnalysisTask task)
    {
        var system = """
            You are an AEVRIX analysis specialist operating inside an evidence-governed pipeline.
            Use only the supplied evidence identifiers and context. Do not claim trusted status.
            Return exactly one JSON object with these properties:
            statement (string), confidence (number 0..1), risk (Low|Medium|High|Critical),
            evidenceIds (array), assumptions (array), openQuestions (array).
            Every evidence id you cite must come from the allowed evidenceIds list.
            """;

        var user = JsonSerializer.Serialize(
            new
            {
                task.TaskId,
                task.ProjectId,
                task.TargetId,
                task.Objective,
                evidenceIds = task.EvidenceIds,
                context = task.Context
            },
            JsonOptions);

        return new OllamaChatRequest(
            _options.Model,
            new[]
            {
                new OllamaChatMessage("system", system),
                new OllamaChatMessage("user", user)
            },
            Stream: false,
            Format: "json");
    }

    private static async Task<string> ReadBoundedUtf8Async(
        Stream stream,
        int maximumBytes,
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

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Ollama response exceeded the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] string Format);

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class OllamaChatEnvelope
    {
        public string? Model { get; set; }
        public OllamaMessageEnvelope? Message { get; set; }
    }

    private sealed class OllamaMessageEnvelope
    {
        public string? Content { get; set; }
    }

    private sealed class OllamaAnalysisPayload
    {
        public string? Statement { get; set; }
        public double Confidence { get; set; }
        public string? Risk { get; set; }
        public string[]? EvidenceIds { get; set; }
        public string[]? Assumptions { get; set; }
        public string[]? OpenQuestions { get; set; }
    }

    private sealed class OllamaTagsEnvelope
    {
        public OllamaTagModel[]? Models { get; set; }
    }

    private sealed class OllamaTagModel
    {
        public string? Name { get; set; }
        public long? Size { get; set; }
        public string? Digest { get; set; }
    }
}
