using System.Security.Cryptography;
using System.Text.Json;
using Aevrix.Core;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace AEVRIX.Desktop;

public sealed class WebView2ResearchBrowserLoginFormAdapter : IResearchBrowserCredentialPacketAdapter
{
    private readonly CoreWebView2 _core;
    private readonly CoreWebView2Environment _environment;
    private readonly ResearchBrowserPolicy _policy;
    private readonly TimeSpan _operationTimeout;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public WebView2ResearchBrowserLoginFormAdapter(CoreWebView2 core, CoreWebView2Environment environment, ResearchBrowserPolicy policy, TimeSpan? operationTimeout = null)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _policy = policy?.Validate() ?? throw new ArgumentNullException(nameof(policy));
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(8);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(operationTimeout));
    }

    public Uri? CurrentUri => Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri) ? uri : null;

    public async Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginUri);
        var decision = ResearchBrowserNavigationGate.Evaluate(_policy, loginUri);
        if (!decision.Allowed) throw new InvalidOperationException($"Login navigation blocked by browser policy: {decision.Code}.");

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) => completion.TrySetResult(args.IsSuccess);
            _core.NavigationCompleted += OnCompleted;
            try
            {
                _core.Navigate(loginUri.AbsoluteUri);
                if (!await completion.Task.WaitAsync(_operationTimeout, cancellationToken)) throw new InvalidOperationException("WebView2 login navigation did not complete successfully.");
                var current = CurrentUri ?? throw new InvalidOperationException("WebView2 login navigation has no final URI.");
                var finalDecision = ResearchBrowserNavigationGate.Evaluate(_policy, current);
                if (!finalDecision.Allowed) throw new InvalidOperationException($"WebView2 login navigation left the governed boundary: {finalDecision.Code}.");
                if (!string.Equals(ProjectCredentialVault.CanonicalizeLoginUri(current), ProjectCredentialVault.CanonicalizeLoginUri(loginUri), StringComparison.Ordinal))
                    throw new InvalidOperationException("WebView2 login navigation ended on a different canonical login URI.");
            }
            finally { _core.NavigationCompleted -= OnCompleted; }
        }
        finally { _operationGate.Release(); }
    }

    public async Task FillCredentialsAndSubmitAsync(LoginRecipe recipe, ReadOnlyMemory<char> userName, ReadOnlyMemory<char> password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Validate();
        if (!string.Equals(recipe.TargetId, _policy.TargetId, StringComparison.Ordinal)) throw new InvalidOperationException("Login recipe target does not match the active WebView2 policy.");
        var recipeDecision = ResearchBrowserNavigationGate.Evaluate(_policy, recipe.LoginUri);
        if (!recipeDecision.Allowed) throw new InvalidOperationException($"Login recipe URI is outside the WebView2 boundary: {recipeDecision.Code}.");
        var current = CurrentUri ?? throw new InvalidOperationException("WebView2 has no current login URI.");
        if (!string.Equals(ProjectCredentialVault.CanonicalizeLoginUri(current), ProjectCredentialVault.CanonicalizeLoginUri(recipe.LoginUri), StringComparison.Ordinal))
            throw new InvalidOperationException("WebView2 is not currently at the canonical login URI.");
        ValidateSelector(recipe.UsernameSelector, nameof(recipe.UsernameSelector));
        ValidateSelector(recipe.PasswordSelector, nameof(recipe.PasswordSelector));
        ValidateSelector(recipe.SubmitSelector, nameof(recipe.SubmitSelector));

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _core.ExecuteScriptAsync(WebView2LoginSharedBufferBootstrapScript.Script);
            cancellationToken.ThrowIfCancellationRequested();

            using var packet = ProjectLoginSecretPacket.Create(userName, password);
            using var sharedBuffer = _environment.CreateSharedBuffer(checked((ulong)packet.Length));
            await WritePacketAsync(sharedBuffer, packet, cancellationToken);

            var nonce = Guid.NewGuid().ToString("N");
            var metadata = JsonSerializer.Serialize(new
            {
                kind = WebView2LoginSharedBufferBootstrapScript.RequestKind,
                nonce,
                usernameSelector = recipe.UsernameSelector,
                passwordSelector = recipe.PasswordSelector,
                submitSelector = recipe.SubmitSelector
            });

            var acknowledgement = new TaskCompletionSource<RendererAcknowledgement>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
            {
                if (IsMessageFromCurrentGovernedPage(args.Source) && TryParseAcknowledgement(args.WebMessageAsJson, nonce, out var parsed)) acknowledgement.TrySetResult(parsed);
            }

            _core.WebMessageReceived += OnWebMessage;
            try
            {
                _core.PostSharedBufferToScript(sharedBuffer, CoreWebView2SharedBufferAccess.ReadOnly, metadata);
                var result = await acknowledgement.Task.WaitAsync(_operationTimeout, cancellationToken);
                if (!result.Ok) throw new InvalidOperationException($"WebView2 renderer rejected login packet: {result.Code}.");
            }
            finally
            {
                _core.WebMessageReceived -= OnWebMessage;
                await ZeroSharedBufferAsync(sharedBuffer, packet.Length);
            }
        }
        finally { _operationGate.Release(); }
    }

    public Task FillAsync(string selector, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("WebView2 credential entry requires the atomic shared-buffer adapter path.");

    public Task SubmitAsync(string selector, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("WebView2 credential entry requires the atomic shared-buffer adapter path.");

    private static async Task WritePacketAsync(CoreWebView2SharedBuffer sharedBuffer, ProjectLoginSecretPacket packet, CancellationToken cancellationToken)
    {
        var transient = packet.Data.ToArray();
        try
        {
            using var stream = sharedBuffer.OpenStream();
            stream.Seek(0);
            using var writer = new DataWriter(stream);
            writer.WriteBytes(transient);
            _ = await writer.StoreAsync();
            _ = await writer.FlushAsync();
            writer.DetachStream();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transient);
        }
    }

    private static async Task ZeroSharedBufferAsync(CoreWebView2SharedBuffer sharedBuffer, int length)
    {
        try
        {
            using var stream = sharedBuffer.OpenStream();
            stream.Seek(0);
            using var writer = new DataWriter(stream);
            writer.WriteBytes(new byte[length]);
            _ = await writer.StoreAsync();
            _ = await writer.FlushAsync();
            writer.DetachStream();
        }
        catch
        {
            // The shared mapping may already be disconnected. Disposal still releases it; do not mask the primary result.
        }
    }

    private bool IsMessageFromCurrentGovernedPage(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)) return false;
        var decision = ResearchBrowserNavigationGate.Evaluate(_policy, sourceUri);
        if (!decision.Allowed || CurrentUri is not Uri current) return false;
        return string.Equals(sourceUri.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceUri.Host, current.Host, StringComparison.OrdinalIgnoreCase)
            && sourceUri.Port == current.Port;
    }

    private static bool TryParseAcknowledgement(string json, string expectedNonce, out RendererAcknowledgement acknowledgement)
    {
        acknowledgement = default;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || !root.TryGetProperty("nonce", out var nonce)
                || !root.TryGetProperty("ok", out var ok)
                || !root.TryGetProperty("code", out var code)
                || root.EnumerateObject().Count() != 4
                || !string.Equals(type.GetString(), WebView2LoginSharedBufferBootstrapScript.ResultMessageType, StringComparison.Ordinal)
                || !string.Equals(nonce.GetString(), expectedNonce, StringComparison.Ordinal)
                || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || code.ValueKind != JsonValueKind.String) return false;
            var codeText = code.GetString();
            if (string.IsNullOrWhiteSpace(codeText) || codeText.Length > 80 || codeText.Any(char.IsControl)) return false;
            acknowledgement = new RendererAcknowledgement(ok.GetBoolean(), codeText);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void ValidateSelector(string selector, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector, parameterName);
        if (selector.Length > 512 || selector.Any(char.IsControl)) throw new ArgumentException("Login selector is invalid.", parameterName);
    }

    private readonly record struct RendererAcknowledgement(bool Ok, string Code);
}