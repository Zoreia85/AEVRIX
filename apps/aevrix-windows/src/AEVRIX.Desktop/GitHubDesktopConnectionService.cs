using System.Net.Http.Headers;
using System.Text.Json;

namespace AEVRIX.Desktop;

internal sealed record GitHubDeviceCode(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    int ExpiresInSeconds,
    int PollIntervalSeconds);

internal sealed record GitHubConnectionSnapshot(
    bool ApiReachable,
    bool Authenticated,
    string? Login,
    string? CanonicalSha,
    string Status,
    DateTimeOffset CheckedAtUtc);

internal sealed class GitHubDesktopConnectionService
{
    private const string TokenCredentialTarget = "AEVRIX:GitHub:UserAccessToken";
    private const string RepositoryApi = "https://api.github.com/repos/Zoreia85/AEVRIX/branches/main";
    private readonly HttpClient _httpClient;

    public GitHubDesktopConnectionService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AEVRIX-Desktop/0.0.2");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public bool HasStoredToken()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(WindowsCredentialSecretStore.Read(TokenCredentialTarget));
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitHubConnectionSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var token = WindowsCredentialSecretStore.Read(TokenCredentialTarget);
        try
        {
            using var branchRequest = CreateRequest(HttpMethod.Get, RepositoryApi, token);
            using var branchResponse = await _httpClient.SendAsync(branchRequest, cancellationToken).ConfigureAwait(false);
            if (!branchResponse.IsSuccessStatusCode)
            {
                return new GitHubConnectionSnapshot(
                    false,
                    false,
                    null,
                    null,
                    $"GitHub respondeu HTTP {(int)branchResponse.StatusCode}; conexão operacional não comprovada.",
                    DateTimeOffset.UtcNow);
            }

            var branchJson = await branchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var branchDocument = JsonDocument.Parse(branchJson);
            var sha = branchDocument.RootElement
                .GetProperty("commit")
                .GetProperty("sha")
                .GetString();

            if (string.IsNullOrWhiteSpace(token))
            {
                return new GitHubConnectionSnapshot(
                    true,
                    false,
                    null,
                    sha,
                    "Repositório público alcançável. Autenticação operacional ainda não configurada.",
                    DateTimeOffset.UtcNow);
            }

            using var userRequest = CreateRequest(HttpMethod.Get, "https://api.github.com/user", token);
            using var userResponse = await _httpClient.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
            if (!userResponse.IsSuccessStatusCode)
            {
                return new GitHubConnectionSnapshot(
                    true,
                    false,
                    null,
                    sha,
                    "Token armazenado, mas o GitHub não confirmou a identidade. Reconecte a conta.",
                    DateTimeOffset.UtcNow);
            }

            var userJson = await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var userDocument = JsonDocument.Parse(userJson);
            var login = userDocument.RootElement.GetProperty("login").GetString();
            return new GitHubConnectionSnapshot(
                true,
                true,
                login,
                sha,
                $"GitHub autenticado como {login}; main canônico alcançável.",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new GitHubConnectionSnapshot(
                false,
                false,
                null,
                null,
                $"GitHub indisponível ({ex.GetType().Name}).",
                DateTimeOffset.UtcNow);
        }
    }

    public async Task<GitHubDeviceCode> RequestDeviceCodeAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim()
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new GitHubDeviceCode(
            root.GetProperty("device_code").GetString()
                ?? throw new InvalidDataException("GitHub did not return device_code."),
            root.GetProperty("user_code").GetString()
                ?? throw new InvalidDataException("GitHub did not return user_code."),
            new Uri(root.GetProperty("verification_uri").GetString()
                ?? throw new InvalidDataException("GitHub did not return verification_uri.")),
            root.GetProperty("expires_in").GetInt32(),
            Math.Max(5, root.GetProperty("interval").GetInt32()));
    }

    public async Task<string> CompleteDeviceFlowAsync(
        string clientId,
        GitHubDeviceCode deviceCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(deviceCode);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(deviceCode.PollIntervalSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId.Trim(),
                    ["device_code"] = deviceCode.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("access_token", out var tokenElement))
            {
                var token = tokenElement.GetString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new InvalidDataException("GitHub returned an empty access token.");
                }

                WindowsCredentialSecretStore.Write(TokenCredentialTarget, token);
                return token;
            }

            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    throw new InvalidOperationException("A autorização do GitHub foi cancelada pelo usuário.");
                case "expired_token":
                    throw new TimeoutException("O código de conexão do GitHub expirou.");
                default:
                    throw new InvalidOperationException($"GitHub Device Flow falhou: {error ?? "resposta inesperada"}.");
            }
        }

        throw new TimeoutException("O tempo para autorizar o GitHub expirou.");
    }

    public void Disconnect()
        => WindowsCredentialSecretStore.Delete(TokenCredentialTarget);

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string? token)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }
}
