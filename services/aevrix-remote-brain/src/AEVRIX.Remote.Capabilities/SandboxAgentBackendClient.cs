using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevrix.Remote.Capabilities;

public enum AgentJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed record SandboxAgentBackendClientOptions(
    TimeSpan RequestTimeout,
    TimeSpan MaximumJobRuntime,
    int MaximumResponseBytes = 2_097_152)
{
    public SandboxAgentBackendClientOptions Validate()
    {
        if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaximumJobRuntime < TimeSpan.FromSeconds(5) || MaximumJobRuntime > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumJobRuntime));
        }

        if (MaximumResponseBytes is < 1_024 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }

        return this;
    }
}

public sealed record AgentWorkRequest(
    string WorkId,
    string Objective,
    string ProjectRoot,
    IReadOnlyList<string> EvidenceIds)
{
    public AgentWorkRequest Validate()
    {
        McpServerDescriptor.ValidateId(WorkId, nameof(WorkId));
        if (string.IsNullOrWhiteSpace(Objective) || Objective.Length > 32_768)
        {
            throw new ArgumentException("Agent objective is missing or exceeds the bounded request size.", nameof(Objective));
        }

        ValidatePortableAbsoluteRoot(ProjectRoot, nameof(ProjectRoot));
        ArgumentNullException.ThrowIfNull(EvidenceIds);
        if (EvidenceIds.Count > 4_096
            || EvidenceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256))
        {
            throw new ArgumentException("Agent evidence identifiers are invalid or exceed bounded limits.", nameof(EvidenceIds));
        }

        return this;
    }

    internal static string NormalizePortablePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 2_048 || value.Any(ch => char.IsControl(ch)))
        {
            throw new ArgumentException("Project path is invalid.", nameof(value));
        }

        var replaced = value.Replace('\\', '/');
        var isWindowsDrive = replaced.Length >= 3
            && char.IsAsciiLetter(replaced[0])
            && replaced[1] == ':'
            && replaced[2] == '/';
        var isPosixAbsolute = replaced.StartsWith("/", StringComparison.Ordinal);
        if (!isWindowsDrive && !isPosixAbsolute)
        {
            throw new ArgumentException("Project path must be absolute.", nameof(value));
        }

        var prefix = isWindowsDrive ? replaced[..3] : "/";
        var remainder = isWindowsDrive ? replaced[3..] : replaced[1..];
        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Project path traversal segments are forbidden.", nameof(value));
        }

        return prefix + string.Join('/', segments);
    }

    private static void ValidatePortableAbsoluteRoot(string value, string parameterName)
    {
        try
        {
            _ = NormalizePortablePath(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Agent project root is invalid.", parameterName, exception);
        }
    }
}

public sealed record AgentJobReceipt(
    string JobId,
    AgentJobState State,
    DateTimeOffset AcceptedAt);

public sealed record AgentIsolationAttestation(
    AgentIsolationLevel Isolation,
    bool HostFilesystemMounted,
    bool OutboundNetworkAllowed,
    string ProjectRoot);

public sealed record AgentJobResult(
    string JobId,
    AgentJobState State,
    AgentIsolationAttestation Attestation,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> EvidenceIds,
    string? OutputSummary,
    string? ArtifactManifestSha256,
    DateTimeOffset ObservedAt);

/// <summary>
/// Client for an AEVRIX-owned sandbox-agent contract. Third-party coding agents can be
/// placed behind this adapter, but cannot expand isolation, project roots, host filesystem,
/// or network privileges beyond the approved AgentBackendDescriptor.
/// </summary>
public sealed class SandboxAgentBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly AgentBackendDescriptor _backend;
    private readonly SandboxAgentBackendClientOptions _options;

    public SandboxAgentBackendClient(
        HttpClient httpClient,
        AgentBackendDescriptor backend,
        SandboxAgentBackendClientOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();

        if (!_backend.CanRun())
        {
            throw new InvalidOperationException(
                $"Agent backend '{_backend.BackendId}' does not satisfy the AEVRIX sandbox execution policy.");
        }
    }

    public string BackendId => _backend.BackendId;

    public async Task<AgentJobReceipt> SubmitAsync(
        AgentWorkRequest work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        work.Validate();
        var normalizedRoot = EnsureAllowedProjectRoot(work.ProjectRoot);
        var body = new AgentSubmitEnvelope(
            work.WorkId,
            work.Objective,
            normalizedRoot,
            work.EvidenceIds.ToArray(),
            new AgentPolicyEnvelope(
                _backend.Isolation,
                HostFilesystemMounted: false,
                OutboundNetworkAllowed: _backend.OutboundNetworkAllowed,
                MaximumRuntimeSeconds: checked((int)_options.MaximumJobRuntime.TotalSeconds)));

        using var response = await SendJsonAsync(
            HttpMethod.Post,
            BuildEndpoint("v1/jobs"),
            body,
            cancellationToken);
        EnsureSuccess(response);
        var receipt = await DeserializeAsync<AgentJobReceiptEnvelope>(response, cancellationToken);
        var result = new AgentJobReceipt(
            ValidateStableId(receipt.JobId, "job id"),
            receipt.State,
            receipt.AcceptedAt);

        if (result.AcceptedAt == default || result.State is not (AgentJobState.Queued or AgentJobState.Running))
        {
            throw new InvalidDataException("Agent backend returned an invalid job receipt.");
        }

        return result;
    }

    public async Task<AgentJobResult> GetResultAsync(
        string jobId,
        string expectedProjectRoot,
        CancellationToken cancellationToken = default)
    {
        var safeJobId = ValidateStableId(jobId, nameof(jobId));
        var normalizedRoot = EnsureAllowedProjectRoot(expectedProjectRoot);
        using var response = await SendJsonAsync(
            HttpMethod.Get,
            BuildEndpoint($"v1/jobs/{Uri.EscapeDataString(safeJobId)}"),
            body: null,
            cancellationToken);
        EnsureSuccess(response);
        var envelope = await DeserializeAsync<AgentJobResultEnvelope>(response, cancellationToken);
        return ValidateResult(envelope, safeJobId, normalizedRoot);
    }

    private AgentJobResult ValidateResult(
        AgentJobResultEnvelope envelope,
        string expectedJobId,
        string expectedProjectRoot)
    {
        var jobId = ValidateStableId(envelope.JobId, "job id");
        if (!string.Equals(jobId, expectedJobId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Agent backend result job id does not match the requested job.");
        }

        if (envelope.ObservedAt == default)
        {
            throw new InvalidDataException("Agent backend result observation timestamp is missing.");
        }

        if (envelope.Attestation is null)
        {
            throw new InvalidDataException("Agent backend result is missing isolation attestation.");
        }

        var attestedRoot = AgentWorkRequest.NormalizePortablePath(envelope.Attestation.ProjectRoot);
        if (envelope.Attestation.Isolation != _backend.Isolation
            || envelope.Attestation.HostFilesystemMounted
            || (!_backend.OutboundNetworkAllowed && envelope.Attestation.OutboundNetworkAllowed)
            || !string.Equals(attestedRoot, expectedProjectRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Agent backend isolation attestation does not match the approved execution policy.");
        }

        var changedFiles = ValidateChangedFiles(envelope.ChangedFiles ?? Array.Empty<string>());
        var evidenceIds = envelope.EvidenceIds ?? Array.Empty<string>();
        if (evidenceIds.Length > 4_096
            || evidenceIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256))
        {
            throw new InvalidDataException("Agent backend returned invalid evidence identifiers.");
        }

        if (envelope.OutputSummary is { Length: > 65_536 })
        {
            throw new InvalidDataException("Agent backend output summary exceeds the bounded size limit.");
        }

        var manifestHash = envelope.ArtifactManifestSha256;
        if (manifestHash is not null && !IsSha256(manifestHash))
        {
            throw new InvalidDataException("Agent backend artifact manifest hash is invalid.");
        }

        if (envelope.State == AgentJobState.Succeeded && manifestHash is null)
        {
            throw new InvalidDataException("Successful agent jobs require an artifact manifest SHA-256.");
        }

        return new AgentJobResult(
            jobId,
            envelope.State,
            new AgentIsolationAttestation(
                envelope.Attestation.Isolation,
                envelope.Attestation.HostFilesystemMounted,
                envelope.Attestation.OutboundNetworkAllowed,
                attestedRoot),
            changedFiles,
            evidenceIds.ToArray(),
            envelope.OutputSummary,
            manifestHash,
            envelope.ObservedAt);
    }

    private string EnsureAllowedProjectRoot(string requestedRoot)
    {
        var normalized = AgentWorkRequest.NormalizePortablePath(requestedRoot);
        foreach (var allowedRoot in _backend.AllowedProjectRoots)
        {
            var allowed = AgentWorkRequest.NormalizePortablePath(allowedRoot);
            if (string.Equals(normalized, allowed, StringComparison.Ordinal)
                || normalized.StartsWith(allowed.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                return normalized;
            }
        }

        throw new InvalidOperationException(
            $"Project root '{normalized}' is outside the backend allowlist.");
    }

    private static IReadOnlyList<string> ValidateChangedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count > 2_048)
        {
            throw new InvalidDataException("Agent backend changed-file list exceeds the bounded limit.");
        }

        var validated = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2_048 || raw.Any(char.IsControl))
            {
                throw new InvalidDataException("Agent backend returned an invalid changed-file path.");
            }

            var path = raw.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal)
                || (path.Length >= 2 && path[1] == ':')
                || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            {
                throw new InvalidDataException("Agent backend changed files must be relative and traversal-free.");
            }

            if (seen.Add(path))
            {
                validated.Add(path);
            }
        }

        validated.Sort(StringComparer.Ordinal);
        return validated;
    }

    private async Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method,
        Uri endpoint,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-AEVRIX-Agent-Contract", "1");
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    private async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Agent backend response must use application/json.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > _options.MaximumResponseBytes)
            {
                throw new InvalidDataException("Agent backend response exceeded the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Agent backend response body is empty or malformed.");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Agent backend returned HTTP {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
        }
    }

    private Uri BuildEndpoint(string relativePath)
    {
        var baseText = _backend.Endpoint.AbsoluteUri.TrimEnd('/') + "/";
        return new Uri(new Uri(baseText, UriKind.Absolute), relativePath);
    }

    private static string ValidateStableId(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 120
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
        {
            throw new InvalidDataException($"Agent backend {fieldName} is invalid.");
        }

        return value;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record AgentSubmitEnvelope(
        string WorkId,
        string Objective,
        string ProjectRoot,
        string[] EvidenceIds,
        AgentPolicyEnvelope Policy);

    private sealed record AgentPolicyEnvelope(
        AgentIsolationLevel Isolation,
        bool HostFilesystemMounted,
        bool OutboundNetworkAllowed,
        int MaximumRuntimeSeconds);

    private sealed class AgentJobReceiptEnvelope
    {
        public string? JobId { get; set; }
        public AgentJobState State { get; set; }
        public DateTimeOffset AcceptedAt { get; set; }
    }

    private sealed class AgentJobResultEnvelope
    {
        public string? JobId { get; set; }
        public AgentJobState State { get; set; }
        public AgentIsolationAttestationEnvelope? Attestation { get; set; }
        public string[]? ChangedFiles { get; set; }
        public string[]? EvidenceIds { get; set; }
        public string? OutputSummary { get; set; }
        public string? ArtifactManifestSha256 { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
    }

    private sealed class AgentIsolationAttestationEnvelope
    {
        public AgentIsolationLevel Isolation { get; set; }
        public bool HostFilesystemMounted { get; set; }
        public bool OutboundNetworkAllowed { get; set; }
        public string ProjectRoot { get; set; } = string.Empty;
    }
}
