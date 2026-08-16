using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Remote.Orchestration;

public sealed record ExecutionAuthorityCredential(
    string ClientId,
    ReadOnlyMemory<byte> Secret);

public interface IExecutionAuthorityCredentialProvider
{
    ValueTask<ExecutionAuthorityCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default);
}

public sealed record RemoteExecutionAuthorityClientOptions(
    Uri Endpoint,
    TimeSpan RequestTimeout,
    string ExpectedSigningKeyId,
    string SigningPublicKeyPem,
    int MaximumResponseBytes = 65_536,
    bool AllowLoopbackHttp = false,
    TimeSpan? MaximumAttestationLifetime = null)
{
    public RemoteExecutionAuthorityClientOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoint);
        if (!Endpoint.IsAbsoluteUri
            || !string.IsNullOrEmpty(Endpoint.UserInfo)
            || !string.IsNullOrEmpty(Endpoint.Query)
            || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new ArgumentException("Execution Authority endpoint must be an absolute clean origin URI.", nameof(Endpoint));
        }

        var isHttps = string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isAllowedLoopbackHttp = AllowLoopbackHttp
            && string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && Endpoint.IsLoopback;
        if (!isHttps && !isAllowedLoopbackHttp)
        {
            throw new InvalidOperationException("Remote Execution Authority requires HTTPS; HTTP is test-only on loopback.");
        }

        if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaximumResponseBytes is < 1_024 or > 1_048_576)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        }

        ValidateToken(ExpectedSigningKeyId, nameof(ExpectedSigningKeyId), 3, 120);
        ArgumentException.ThrowIfNullOrWhiteSpace(SigningPublicKeyPem);
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(SigningPublicKeyPem);
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("Execution Authority public signing key is invalid.", nameof(SigningPublicKeyPem), exception);
        }

        if (key.KeySize != 256)
        {
            throw new ArgumentException("Execution Authority signing key must be ECDSA P-256.", nameof(SigningPublicKeyPem));
        }

        var lifetime = MaximumAttestationLifetime ?? TimeSpan.FromMinutes(10);
        if (lifetime < TimeSpan.FromSeconds(30) || lifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttestationLifetime));
        }

        return this with { MaximumAttestationLifetime = lifetime };
    }

    internal static void ValidateToken(string value, string name, int min, int max)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < min
            || value.Length > max
            || value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException($"Execution Authority {name} is invalid.", name);
        }
    }
}

public sealed record PromotionAuthorityAttestation(
    int Version,
    string KeyId,
    Guid ProjectId,
    string RunId,
    string ExecutionId,
    string EvidenceDigestSha256,
    long HeadEntryCount,
    string HeadHashSha256,
    long IssuedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    string Nonce,
    string SignatureDerBase64,
    string PublicKeySpkiSha256)
{
    public const int CurrentVersion = 1;
    public const string ProtocolLabel = "AEVRIX-PROMOTION-ATTESTATION-V1";

    public byte[] CanonicalPayloadUtf8()
    {
        ValidateStructural();
        var canonical = string.Join("\n", new[]
        {
            ProtocolLabel,
            Version.ToString(CultureInfo.InvariantCulture),
            KeyId,
            ProjectId.ToString("D"),
            RunId,
            ExecutionId,
            EvidenceDigestSha256.ToLowerInvariant(),
            HeadEntryCount.ToString(CultureInfo.InvariantCulture),
            HeadHashSha256.ToLowerInvariant(),
            IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            ExpiresAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            Nonce
        });
        return Encoding.UTF8.GetBytes(canonical);
    }

    internal void ValidateStructural()
    {
        if (Version != CurrentVersion) throw new InvalidDataException("Execution Authority attestation version is unsupported.");
        RemoteExecutionAuthorityClientOptions.ValidateToken(KeyId, nameof(KeyId), 3, 120);
        if (ProjectId == Guid.Empty) throw new InvalidDataException("Execution Authority attestation project id is empty.");
        ExecutionProofEvent.ValidateSafeId(RunId, nameof(RunId), 3, 160);
        ExecutionProofEvent.ValidateSafeId(ExecutionId, nameof(ExecutionId), 3, 160);
        ExecutionProofEvent.ValidateSha256(EvidenceDigestSha256, nameof(EvidenceDigestSha256), required: true);
        ExecutionProofEvent.ValidateSha256(HeadHashSha256, nameof(HeadHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(PublicKeySpkiSha256, nameof(PublicKeySpkiSha256), required: true);
        if (HeadEntryCount <= 0) throw new InvalidDataException("Execution Authority attestation head count is invalid.");
        if (IssuedAtUnixSeconds <= 0 || ExpiresAtUnixSeconds <= IssuedAtUnixSeconds)
            throw new InvalidDataException("Execution Authority attestation validity window is invalid.");
        RemoteExecutionAuthorityClientOptions.ValidateToken(Nonce, nameof(Nonce), 16, 128);
        if (string.IsNullOrWhiteSpace(SignatureDerBase64) || SignatureDerBase64.Length > 2_048)
            throw new InvalidDataException("Execution Authority attestation signature is invalid.");
    }
}

/// <summary>
/// HTTPS client for the independent Execution Authority trust domain. It implements the monotonic
/// head-anchor contract and verifies promotion attestations locally against a pinned ECDSA P-256
/// public key. Request credentials are supplied at runtime and are never serialized into payloads.
/// </summary>
public sealed class RemoteExecutionAuthorityClient : IExecutionProofHeadAnchor
{
    private const string RequestProtocolLabel = "AEVRIX-AUTHORITY-REQUEST-V1";
    private static readonly byte[] EmptyBodyHash = SHA256.HashData(Array.Empty<byte>());
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IExecutionAuthorityCredentialProvider _credentials;
    private readonly RemoteExecutionAuthorityClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Uri _baseUri;
    private readonly byte[] _publicKeySpkiSha256;

    public RemoteExecutionAuthorityClient(
        HttpClient httpClient,
        IExecutionAuthorityCredentialProvider credentials,
        RemoteExecutionAuthorityClientOptions options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _baseUri = new Uri(_options.Endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);

        using var key = ECDsa.Create();
        key.ImportFromPem(_options.SigningPublicKeyPem);
        _publicKeySpkiSha256 = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
    }

    public async Task<ExecutionProofHead?> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        var relativePath = $"v1/projects/{projectId:D}/head";
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Get,
            relativePath,
            body: null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response, "load execution-proof head");
        var payload = await ReadJsonAsync<HeadEnvelope>(response, cancellationToken).ConfigureAwait(false);
        var head = new ExecutionProofHead(payload.EntryCount, payload.HeadHashSha256 ?? string.Empty);
        ValidateHead(head, allowEmpty: false);
        return head;
    }

    public async Task AdvanceAsync(
        Guid projectId,
        ExecutionProofHead expectedPrevious,
        ExecutionProofHead next,
        CancellationToken cancellationToken = default)
    {
        ValidateProject(projectId);
        ValidateHead(expectedPrevious, allowEmpty: true);
        ValidateHead(next, allowEmpty: false);
        if (next.EntryCount != expectedPrevious.EntryCount + 1)
            throw new InvalidDataException("Execution Authority CAS may advance exactly one record at a time.");

        var body = JsonSerializer.SerializeToUtf8Bytes(new AdvanceEnvelope(
            RequestId: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ExpectedPrevious: new HeadEnvelope(expectedPrevious.EntryCount, expectedPrevious.HeadHashSha256),
            Next: new HeadEnvelope(next.EntryCount, next.HeadHashSha256)), Json);

        var relativePath = $"v1/projects/{projectId:D}/head/advance";
        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post,
            relativePath,
            body,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("Execution Authority rejected the CAS predecessor; rollback, fork or concurrent advance detected.");
        EnsureSuccess(response, "advance execution-proof head");
        var confirmed = await ReadJsonAsync<HeadEnvelope>(response, cancellationToken).ConfigureAwait(false);
        var confirmedHead = new ExecutionProofHead(confirmed.EntryCount, confirmed.HeadHashSha256 ?? string.Empty);
        ValidateHead(confirmedHead, allowEmpty: false);
        if (confirmedHead != next)
            throw new InvalidDataException("Execution Authority did not confirm the exact requested head.");
    }

    public async Task<PromotionAuthorityAttestation> RequestPromotionAttestationAsync(
        PromotionEvidenceEnvelope evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Version != ExecutionProofLedger.CurrentVersion
            || evidence.ProjectId == Guid.Empty
            || evidence.LedgerHead.EntryCount <= 0)
            throw new InvalidDataException("Promotion evidence envelope is structurally invalid.");
        ExecutionProofEvent.ValidateSha256(evidence.AuthorizationRecordHashSha256, nameof(evidence.AuthorizationRecordHashSha256), required: true);
        ExecutionProofEvent.ValidateSha256(evidence.LedgerHead.HeadHashSha256, nameof(evidence.LedgerHead.HeadHashSha256), required: true);
        if (!CryptographicHexEquals(evidence.AuthorizationRecordHashSha256, evidence.LedgerHead.HeadHashSha256))
            throw new InvalidDataException("Promotion authorization record must be the currently anchored ledger head.");

        var evidenceDigest = evidence.ComputeDigestSha256();
        var body = JsonSerializer.SerializeToUtf8Bytes(new PromotionEvidenceRequest(
            evidence.Version,
            evidence.ProjectId,
            evidence.RunId,
            evidence.ExecutionId,
            evidence.CapabilityClass,
            evidence.CapabilityId,
            evidence.ArtifactManifestSha256,
            evidence.ValidationDigestSha256,
            evidence.JudgeDecisionDigestSha256,
            evidence.PromotionDigestSha256,
            evidence.AuthorizationRecordHashSha256,
            new HeadEnvelope(evidence.LedgerHead.EntryCount, evidence.LedgerHead.HeadHashSha256),
            evidenceDigest), Json);

        using var response = await SendAuthenticatedAsync(
            HttpMethod.Post,
            "v1/promotions/attest",
            body,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("Execution Authority refused attestation because the submitted head is not authoritative.");
        EnsureSuccess(response, "attest promotion evidence");

        var attestation = await ReadJsonAsync<PromotionAuthorityAttestation>(response, cancellationToken).ConfigureAwait(false);
        VerifyAttestation(attestation, evidence, evidenceDigest);
        return attestation;
    }

    private void VerifyAttestation(
        PromotionAuthorityAttestation attestation,
        PromotionEvidenceEnvelope evidence,
        string evidenceDigest)
    {
        attestation.ValidateStructural();
        if (!string.Equals(attestation.KeyId, _options.ExpectedSigningKeyId, StringComparison.Ordinal)
            || attestation.ProjectId != evidence.ProjectId
            || !string.Equals(attestation.RunId, evidence.RunId, StringComparison.Ordinal)
            || !string.Equals(attestation.ExecutionId, evidence.ExecutionId, StringComparison.Ordinal)
            || attestation.HeadEntryCount != evidence.LedgerHead.EntryCount
            || !CryptographicHexEquals(attestation.HeadHashSha256, evidence.LedgerHead.HeadHashSha256)
            || !CryptographicHexEquals(attestation.EvidenceDigestSha256, evidenceDigest)
            || !CryptographicHexEquals(attestation.PublicKeySpkiSha256, Convert.ToHexString(_publicKeySpkiSha256)))
        {
            throw new InvalidDataException("Execution Authority attestation is not bound to the requested promotion evidence and pinned key.");
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var maxLifetimeSeconds = checked((long)_options.MaximumAttestationLifetime!.Value.TotalSeconds);
        if (attestation.IssuedAtUnixSeconds > now + 30
            || attestation.ExpiresAtUnixSeconds < now
            || attestation.ExpiresAtUnixSeconds - attestation.IssuedAtUnixSeconds > maxLifetimeSeconds)
        {
            throw new InvalidDataException("Execution Authority attestation is expired, future-dated or exceeds the configured lifetime.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attestation.SignatureDerBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Execution Authority attestation signature is not valid Base64.", exception);
        }

        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(_options.SigningPublicKeyPem);
        var payload = attestation.CanonicalPayloadUtf8();
        try
        {
            if (!publicKey.VerifyData(
                    payload,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new InvalidDataException("Execution Authority attestation signature verification failed.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpMethod method,
        string relativePath,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.StartsWith('/') || relativePath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Execution Authority relative path is invalid.", nameof(relativePath));

        var credential = await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        RemoteExecutionAuthorityClientOptions.ValidateToken(credential.ClientId, nameof(credential.ClientId), 3, 120);
        if (credential.Secret.Length is < 32 or > 256)
            throw new InvalidDataException("Execution Authority request secret must contain 32 to 256 bytes.");
        var secret = credential.Secret.ToArray();
        try
        {
            var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var bodyHash = body is null ? EmptyBodyHash : SHA256.HashData(body);
            var bodyHashHex = Convert.ToHexString(bodyHash).ToLowerInvariant();
            var path = "/" + relativePath;
            var canonical = Encoding.UTF8.GetBytes(string.Join("\n", new[]
            {
                RequestProtocolLabel,
                method.Method.ToUpperInvariant(),
                path,
                timestamp.ToString(CultureInfo.InvariantCulture),
                nonce,
                bodyHashHex
            }));
            byte[] signature = HMACSHA256.HashData(secret, canonical);
            try
            {
                using var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("X-AEVRIX-Client-Id", credential.ClientId);
                request.Headers.TryAddWithoutValidation("X-AEVRIX-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation("X-AEVRIX-Nonce", nonce);
                request.Headers.TryAddWithoutValidation("X-AEVRIX-Body-SHA256", bodyHashHex);
                request.Headers.TryAddWithoutValidation("X-AEVRIX-Request-Signature", Convert.ToBase64String(signature));
                if (body is not null)
                    request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
                CryptographicOperations.ZeroMemory(canonical);
                if (body is not null) CryptographicOperations.ZeroMemory(bodyHash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Execution Authority response must use application/json.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > _options.MaximumResponseBytes)
                throw new InvalidDataException("Execution Authority response exceeded the configured size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json)
                ?? throw new InvalidDataException("Execution Authority response body is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Execution Authority response JSON is malformed.", exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Execution Authority could not {operation}; HTTP {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
    }

    private static void ValidateProject(Guid projectId)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
    }

    private static void ValidateHead(ExecutionProofHead head, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (allowEmpty && head == ExecutionProofHead.Empty) return;
        if (head.EntryCount <= 0) throw new InvalidDataException("Execution Authority head count is invalid.");
        ExecutionProofEvent.ValidateSha256(head.HeadHashSha256, nameof(head.HeadHashSha256), required: true);
        if (CryptographicHexEquals(head.HeadHashSha256, ExecutionProofLedger.GenesisHash))
            throw new InvalidDataException("A non-empty Execution Authority head cannot use the genesis hash.");
    }

    private static bool CryptographicHexEquals(string left, string right)
    {
        ExecutionProofEvent.ValidateSha256(left, nameof(left), required: true);
        ExecutionProofEvent.ValidateSha256(right, nameof(right), required: true);
        var a = Convert.FromHexString(left);
        var b = Convert.FromHexString(right);
        try { return CryptographicOperations.FixedTimeEquals(a, b); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }

    private sealed record HeadEnvelope(long EntryCount, string? HeadHashSha256);
    private sealed record AdvanceEnvelope(string RequestId, HeadEnvelope ExpectedPrevious, HeadEnvelope Next);
    private sealed record PromotionEvidenceRequest(
        int Version,
        Guid ProjectId,
        string RunId,
        string ExecutionId,
        string CapabilityClass,
        string CapabilityId,
        string ArtifactManifestSha256,
        string ValidationDigestSha256,
        string JudgeDecisionDigestSha256,
        string PromotionDigestSha256,
        string AuthorizationRecordHashSha256,
        HeadEnvelope LedgerHead,
        string EvidenceDigestSha256);
}
