using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevrix.Remote.Orchestration;

public sealed record LocalModelProviderPolicy(
    IReadOnlySet<string> AllowedModels,
    int MaximumPromptCharacters = 32_000,
    int MaximumResponseCharacters = 64_000,
    TimeSpan? RequestTimeout = null)
{
    public LocalModelProviderPolicy Validate()
    {
        if (AllowedModels is null || AllowedModels.Count == 0 || AllowedModels.Count > 128
            || AllowedModels.Any(model => string.IsNullOrWhiteSpace(model) || model.Length > 160))
        {
            throw new ArgumentException("A bounded local-model allowlist is required.", nameof(AllowedModels));
        }

        if (MaximumPromptCharacters is < 256 or > 128_000
            || MaximumResponseCharacters is < 256 or > 256_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPromptCharacters));
        }

        var timeout = RequestTimeout ?? TimeSpan.FromSeconds(45);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        return this with { RequestTimeout = timeout };
    }
}

public sealed record LocalModelCapabilities(
    string ProviderId,
    IReadOnlyList<string> AvailableAllowedModels,
    bool Healthy);

public interface ILocalModelProvider : IAevrixModelProvider
{
    Task<LocalModelCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional Ollama REST adapter. Ollama is a replaceable local runtime, never the AEVRIX brain.
/// It is fail-closed: loopback HTTP only, explicit model allowlist, bounded I/O, no pull/download,
/// and every model output remains candidate knowledge for independent Judge validation.
/// </summary>
public sealed class OllamaLocalModelProvider : ILocalModelProvider, IDisposable
{
    public const string ModelContextKey = "local-model";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpMessageInvoker _transport;
    private readonly Uri _baseAddress;
    private readonly LocalModelProviderPolicy _policy;
    private readonly bool _ownsTransport;

    public OllamaLocalModelProvider(
        HttpMessageInvoker transport,
        LocalModelProviderPolicy policy,
        Uri? baseAddress = null,
        bool ownsTransport = false)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
        _baseAddress = baseAddress ?? new Uri("http://127.0.0.1:11434/");
        ValidateLoopbackEndpoint(_baseAddress);
        _ownsTransport = ownsTransport;
    }

    public string ProviderId => "ollama-local-rest";

    public async Task<LocalModelCapabilities> DiscoverCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CreateTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, Resolve("api/tags"));
        using var response = await _transport.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            return new LocalModelCapabilities(ProviderId, Array.Empty<string>(), false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var payload = await JsonSerializer.DeserializeAsync<OllamaTagsResponse>(stream, JsonOptions, timeout.Token);
        var allowed = (payload?.Models ?? Array.Empty<OllamaModel>())
            .Select(model => model.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name) && _policy.AllowedModels.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return new LocalModelCapabilities(ProviderId, allowed, true);
    }

    public async Task<ModelAnalysisCandidate> AnalyzeAsync(
        AnalysisTask task,
        CancellationToken cancellationToken = default)
    {
        task.Validate();

        if (!task.Context.TryGetValue(ModelContextKey, out var model)
            || string.IsNullOrWhiteSpace(model)
            || !_policy.AllowedModels.Contains(model))
        {
            throw new InvalidOperationException("Requested local model is not explicitly allowlisted.");
        }

        var prompt = BuildPrompt(task);
        if (prompt.Length > _policy.MaximumPromptCharacters)
        {
            throw new InvalidDataException("Local-model prompt exceeds the governed size limit.");
        }

        using var timeout = CreateTimeout(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve("api/generate"))
        {
            Content = JsonContent.Create(new OllamaGenerateRequest(model, prompt, Stream: false), options: JsonOptions)
        };
        using var response = await _transport.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long length
            && length > _policy.MaximumResponseCharacters * 4L)
        {
            throw new InvalidDataException("Local-model response body exceeds the governed size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var payload = await JsonSerializer.DeserializeAsync<OllamaGenerateResponse>(stream, JsonOptions, timeout.Token)
            ?? throw new InvalidDataException("Local-model response is empty or invalid JSON.");

        if (!payload.Done || string.IsNullOrWhiteSpace(payload.Response)
            || payload.Response.Length > _policy.MaximumResponseCharacters)
        {
            throw new InvalidDataException("Local-model response is incomplete or exceeds the governed size limit.");
        }

        return new ModelAnalysisCandidate(
            ProviderId,
            string.IsNullOrWhiteSpace(payload.Model) ? model : payload.Model,
            payload.Response.Trim(),
            0.55,
            ModelRiskLevel.High,
            task.EvidenceIds.ToArray(),
            new[] { "Local-model output is untrusted until independently validated by the AEVRIX Judge." },
            new[] { "Does governed evidence independently support this model-generated statement?" });
    }

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_policy.RequestTimeout!.Value);
        return linked;
    }

    private Uri Resolve(string relativePath) => new(_baseAddress, relativePath);

    private static string BuildPrompt(AnalysisTask task) =>
        $"Objective: {task.Objective}\nEvidence IDs: {string.Join(", ", task.EvidenceIds)}\n"
        + "Return analysis only. Do not claim evidence you were not given.";

    private static void ValidateLoopbackEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp
            || endpoint.UserInfo.Length != 0 || !endpoint.IsLoopback
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                "Ollama endpoint must be an absolute loopback HTTP URI without credentials, query or fragment.");
        }
    }

    private sealed record OllamaGenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("done")] bool Done);

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] IReadOnlyList<OllamaModel>? Models);

    private sealed record OllamaModel(
        [property: JsonPropertyName("name")] string Name);
}
