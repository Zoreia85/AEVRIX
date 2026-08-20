using System.Net.Http.Headers;
using System.Text.Json;

namespace AEVRIX.Desktop;

internal sealed record GitHubDeviceCode(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    int ExpiresInSeconds,
    int PollIntervalSeconds);

internal sealed record GitHubActionsSnapshot(
    bool Readable,
    long? LatestRunId,
    string? WorkflowName,
    string? Status,
    string? Conclusion,
    string? HeadSha,
    DateTimeOffset? UpdatedAtUtc,
    string Detail);

internal sealed record GitHubConnectionSnapshot(
    bool ApiReachable,
    bool Authenticated,
    string? Login,
    string? CanonicalSha,
    GitHubActionsSnapshot Actions,
    bool WorkflowDispatchAuthorized,
    string WorkflowDispatchDetail,
    string Status,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? LastSuccessfulSyncAtUtc);

internal sealed class GitHubDesktopConnectionService
{
    private const string TokenCredentialTarget = "AEVRIX:GitHub:UserAccessToken";
    private const string RepositoryApi = "https://api.github.com/repos/Zoreia85/AEVRIX/branches/main";
    private const string ActionsApi = "https://api.github.com/repos/Zoreia85/AEVRIX/actions/runs?branch=main&per_page=10";
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
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
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
        string? token;
        try
        {
            token = WindowsCredentialSecretStore.Read(TokenCredentialTarget);
        }
        catch (Exception ex)
        {
            return Unavailable($"O Windows Credential Manager não pôde ser consultado ({ex.GetType().Name}).");
        }

        try
        {
            using var branchRequest = CreateRequest(HttpMethod.Get, RepositoryApi, token);
            using var branchResponse = await _httpClient.SendAsync(branchRequest, cancellationToken).ConfigureAwait(false);
            if (!branchResponse.IsSuccessStatusCode)
            {
                return Unavailable(
                    $"GitHub respondeu HTTP {(int)branchResponse.StatusCode}; conexão operacional não comprovada.");
            }

            var branchJson = await branchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var branchDocument = JsonDocument.Parse(branchJson);
            var sha = branchDocument.RootElement
                .GetProperty("commit")
                .GetProperty("sha")
                .GetString();

            var actions = await ProbeActionsAsync(token, cancellationToken).ConfigureAwait(false);
            string? login = null;
            var authenticated = false;
            var authenticationDetail = "Autenticação operacional ainda não configurada.";

            if (!string.IsNullOrWhiteSpace(token))
            {
                using var userRequest = CreateRequest(HttpMethod.Get, "https://api.github.com/user", token);
                using var userResponse = await _httpClient.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
                if (userResponse.IsSuccessStatusCode)
                {
                    var userJson = await userResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var userDocument = JsonDocument.Parse(userJson);
                    login = userDocument.RootElement.GetProperty("login").GetString();
                    authenticated = !string.IsNullOrWhiteSpace(login);
                    authenticationDetail = authenticated
                        ? $"Conta autenticada: {login}."
                        : "GitHub respondeu à autenticação sem uma identidade utilizável.";
                }
                else
                {
                    authenticationDetail =
                        $"Token armazenado, mas identidade não confirmada (HTTP {(int)userResponse.StatusCode}). Reconecte a conta.";
                }
            }

            var checkedAt = DateTimeOffset.UtcNow;
            var fullyReadable = actions.Readable && !string.IsNullOrWhiteSpace(sha);
            var status = fullyReadable
                ? $"Repositório e Actions alcançáveis. {authenticationDetail}"
                : $"Repositório alcançável, mas Actions não foi comprovado. {actions.Detail} {authenticationDetail}";

            // A successful GET does not prove Actions:write. GitHub App permissions must be
            // configured and an actual dispatch must succeed before the product can claim it.
            return new GitHubConnectionSnapshot(
                ApiReachable: true,
                Authenticated: authenticated,
                Login: login,
                CanonicalSha: sha,
                Actions: actions,
                WorkflowDispatchAuthorized: false,
                WorkflowDispatchDetail: authenticated
                    ? "Conta autenticada, porém Actions:write/workflow_dispatch ainda não foi comprovado pela GitHub App AEVRIX."
                    : "Conecte a GitHub App AEVRIX para posteriormente comprovar Actions:write/workflow_dispatch.",
                Status: status,
                CheckedAtUtc: checkedAt,
                LastSuccessfulSyncAtUtc: fullyReadable ? checkedAt : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable("GitHub excedeu o tempo limite da verificação operacional.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
        {
            return Unavailable($"GitHub indisponível ({ex.GetType().Name}).");
        }
    }

    private async Task<GitHubActionsSnapshot> ProbeActionsAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, ActionsApi, token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubActionsSnapshot(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"Actions respondeu HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("workflow_runs", out var runs)
                || runs.ValueKind != JsonValueKind.Array)
            {
                return new GitHubActionsSnapshot(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "Resposta de Actions não contém workflow_runs.");
            }

            var enumerator = runs.EnumerateArray();
            if (!enumerator.MoveNext())
            {
                return new GitHubActionsSnapshot(
                    true,
                    null,
                    null,
                    "sem execuções",
                    null,
                    null,
                    null,
                    "Actions está acessível, mas não há execução recente na branch main.");
            }

            var latest = enumerator.Current;
            var runId = latest.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id)
                ? id
                : null;
            var name = GetOptionalString(latest, "name");
            var status = GetOptionalString(latest, "status");
            var conclusion = GetOptionalString(latest, "conclusion");
            var headSha = GetOptionalString(latest, "head_sha");
            DateTimeOffset? updatedAt = null;
            var updated = GetOptionalString(latest, "updated_at");
            if (DateTimeOffset.TryParse(updated, out var parsed))
            {
                updatedAt = parsed;
            }

            return new GitHubActionsSnapshot(
                true,
                runId,
                name,
                status,
                conclusion,
                headSha,
                updatedAt,
                $"Último workflow observado: {name ?? "desconhecido"} / {status ?? "estado desconhecido"} / {conclusion ?? "sem conclusão"}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new GitHubActionsSnapshot(
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                $"Actions indisponível ({ex.GetType().Name}).");
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

    private static GitHubConnectionSnapshot Unavailable(string detail)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        return new GitHubConnectionSnapshot(
            false,
            false,
            null,
            null,
            new GitHubActionsSnapshot(false, null, null, null, null, null, null, "Actions não verificado."),
            false,
            "workflow_dispatch não está disponível sem uma conexão GitHub comprovada.",
            detail,
            checkedAt,
            null);
    }

    private static string? GetOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
