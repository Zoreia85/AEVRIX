using System.Text.Json;
using Aevrix.Core;
using Microsoft.Web.WebView2.Core;

namespace AEVRIX.Desktop;

/// <summary>
/// Hardened Research Browser login adapter. Credential bytes are copied once into a WebView2 shared buffer
/// and posted read-only to the currently governed main-frame document. The page receives selectors only as
/// non-secret metadata. A renderer acknowledgement means that the submit action was dispatched, not that
/// authentication succeeded; post-login state requires a separate outcome judge.
/// </summary>
public sealed class WebView2SharedBufferLoginFormAdapter : IResearchBrowserAtomicLoginFormAdapter
{
    private const string OperationKind = "aevrix.project-login.v1";
    private const string ResultKind = "aevrix.project-login.result.v1";
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    private readonly CoreWebView2 _core;
    private readonly string _targetId;
    private readonly ResearchBrowserPolicy _policy;

    public WebView2SharedBufferLoginFormAdapter(
        CoreWebView2 core,
        string targetId,
        ResearchBrowserPolicy policy)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
        if (!string.Equals(targetId, _policy.TargetId, StringComparison.Ordinal))
        {
            throw new ArgumentException("WebView2 login adapter target does not match browser policy.", nameof(targetId));
        }
        _targetId = targetId;
    }

    public Uri? CurrentUri => TryCurrentUri();

    public async Task NavigateAsync(Uri loginUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loginUri);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAllowed(loginUri, "Login navigation");

        if (CanonicalEquals(CurrentUri, loginUri))
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (!args.IsSuccess)
            {
                completion.TrySetException(new InvalidOperationException(
                    $"WebView2 login navigation failed: {args.WebErrorStatus}."));
                return;
            }

            var current = TryCurrentUri();
            if (CanonicalEquals(current, loginUri))
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new InvalidOperationException(
                    "WebView2 login navigation completed on an unexpected page."));
            }
        };

        _core.NavigationCompleted += handler;
        try
        {
            _core.Navigate(loginUri.AbsoluteUri);
            await completion.Task.WaitAsync(NavigationTimeout, cancellationToken);
        }
        finally
        {
            _core.NavigationCompleted -= handler;
        }
    }

    public async Task FillCredentialsAndSubmitAsync(
        LoginRecipe recipe,
        ReadOnlyMemory<char> userName,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        cancellationToken.ThrowIfCancellationRequested();
        recipe.Validate();

        if (!string.Equals(recipe.TargetId, _targetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Login recipe target does not match the active WebView2 target.");
        }

        var currentUri = CurrentUri
            ?? throw new InvalidOperationException("Research Browser has no active absolute page URI.");
        EnsureAllowed(currentUri, "Login form fill");
        if (!CanonicalEquals(currentUri, recipe.LoginUri))
        {
            throw new InvalidOperationException("Research Browser is not at the canonical login page.");
        }

        var bootstrapResult = await _core.ExecuteScriptAsync(BootstrapScript);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(bootstrapResult))
        {
            throw new InvalidOperationException("WebView2 login bootstrap did not execute.");
        }

        var nonce = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? messageHandler = null;
        messageHandler = (_, args) =>
        {
            try
            {
                if (!Uri.TryCreate(args.Source, UriKind.Absolute, out var sourceUri)
                    || !CanonicalEquals(sourceUri, recipe.LoginUri)
                    || !ResearchBrowserNavigationGate.Evaluate(_policy, sourceUri).Allowed)
                {
                    return;
                }

                string message;
                try
                {
                    message = args.TryGetWebMessageAsString();
                }
                catch (ArgumentException)
                {
                    return;
                }

                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("kind", out var kind)
                    || !root.TryGetProperty("nonce", out var nonceNode)
                    || !root.TryGetProperty("status", out var status)
                    || !root.TryGetProperty("code", out var code))
                {
                    return;
                }

                if (!string.Equals(kind.GetString(), ResultKind, StringComparison.Ordinal)
                    || !string.Equals(nonceNode.GetString(), nonce, StringComparison.Ordinal))
                {
                    return;
                }

                var statusValue = status.GetString();
                var codeValue = code.GetString() ?? "login_adapter_unknown_result";
                if (string.Equals(statusValue, "submitted", StringComparison.Ordinal))
                {
                    result.TrySetResult(codeValue);
                }
                else
                {
                    result.TrySetException(new InvalidOperationException(
                        $"WebView2 login form operation failed: {codeValue}."));
                }
            }
            catch (JsonException)
            {
                // Untrusted page messages that do not match the strict acknowledgement schema are ignored.
            }
        };

        _core.WebMessageReceived += messageHandler;
        try
        {
            using var packet = ProjectLoginSecretPacket.Create(userName, password);
            using var sharedBuffer = _core.Environment.CreateSharedBuffer(checked((ulong)packet.Length));
            using (var stream = sharedBuffer.OpenStream())
            {
                packet.WriteTo(stream);
                stream.Flush();
            }

            packet.Dispose();

            var metadataJson = JsonSerializer.Serialize(new SharedBufferMetadata(
                OperationKind,
                nonce,
                recipe.UsernameSelector,
                recipe.PasswordSelector,
                recipe.SubmitSelector));

            _core.PostSharedBufferToScript(
                sharedBuffer,
                CoreWebView2SharedBufferAccess.ReadOnly,
                metadataJson);

            sharedBuffer.Close();
            _ = await result.Task.WaitAsync(OperationTimeout, cancellationToken);
        }
        finally
        {
            _core.WebMessageReceived -= messageHandler;
        }
    }

    public Task FillAsync(
        string selector,
        ReadOnlyMemory<char> value,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("WebView2 login adapter requires the atomic credential operation.");

    public Task SubmitAsync(string selector, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("WebView2 login adapter requires the atomic credential operation.");

    private void EnsureAllowed(Uri uri, string operation)
    {
        var decision = ResearchBrowserNavigationGate.Evaluate(_policy, uri);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException($"{operation} blocked by browser policy: {decision.Code}.");
        }
    }

    private Uri? TryCurrentUri() =>
        Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri) ? uri : null;

    private static bool CanonicalEquals(Uri? left, Uri right)
    {
        if (left is null || !left.IsAbsoluteUri || !right.IsAbsoluteUri)
        {
            return false;
        }

        try
        {
            return string.Equals(
                ProjectCredentialVault.CanonicalizeLoginUri(left),
                ProjectCredentialVault.CanonicalizeLoginUri(right),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record SharedBufferMetadata(
        string Kind,
        string Nonce,
        string UsernameSelector,
        string PasswordSelector,
        string SubmitSelector);

    private const string BootstrapScript = """
(() => {
  if (!globalThis.chrome || !globalThis.chrome.webview) return 'webview_unavailable';
  if (globalThis.__aevrixProjectLoginSharedBufferV1 === true) return 'already_installed';

  const resultKind = 'aevrix.project-login.result.v1';
  const expectedKind = 'aevrix.project-login.v1';
  const postResult = (nonce, status, code) => {
    globalThis.chrome.webview.postMessage(JSON.stringify({ kind: resultKind, nonce, status, code }));
  };

  const unique = (selector) => {
    const nodes = document.querySelectorAll(selector);
    if (nodes.length !== 1) throw new Error('selector_not_unique');
    return nodes[0];
  };

  const assignInputValue = (input, value) => {
    if (!(input instanceof HTMLInputElement)) throw new Error('target_not_input');
    const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    if (!descriptor || typeof descriptor.set !== 'function') throw new Error('input_value_setter_unavailable');
    descriptor.set.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
  };

  globalThis.chrome.webview.addEventListener('sharedbufferreceived', (event) => {
    const metadata = event.additionalData;
    if (!metadata || metadata.kind !== expectedKind || typeof metadata.nonce !== 'string') return;

    const buffer = event.getBuffer();
    let released = false;
    try {
      const bytes = new Uint8Array(buffer);
      if (bytes.length < 13
          || bytes[0] !== 65 || bytes[1] !== 88 || bytes[2] !== 76 || bytes[3] !== 71
          || bytes[4] !== 1) {
        throw new Error('packet_header_invalid');
      }

      const view = new DataView(buffer);
      const userLength = view.getUint32(5, true);
      const secretLength = view.getUint32(9, true);
      if (13 + userLength + secretLength !== bytes.length || userLength === 0 || secretLength === 0) {
        throw new Error('packet_length_invalid');
      }

      const decoder = new TextDecoder('utf-8', { fatal: true });
      const userName = decoder.decode(bytes.subarray(13, 13 + userLength));
      const secret = decoder.decode(bytes.subarray(13 + userLength));

      globalThis.chrome.webview.releaseBuffer(buffer);
      released = true;

      const userInput = unique(metadata.usernameSelector);
      const secretInput = unique(metadata.passwordSelector);
      const submitControl = unique(metadata.submitSelector);
      if (!(secretInput instanceof HTMLInputElement)
          || String(secretInput.type || '').toLowerCase() !== 'password') {
        throw new Error('password_target_invalid');
      }

      assignInputValue(userInput, userName);
      assignInputValue(secretInput, secret);
      if (!(submitControl instanceof HTMLElement)) throw new Error('submit_target_invalid');
      submitControl.click();
      postResult(metadata.nonce, 'submitted', 'login_submit_dispatched');
    } catch (_) {
      postResult(metadata.nonce, 'failed', 'login_form_fill_failed');
    } finally {
      if (!released) globalThis.chrome.webview.releaseBuffer(buffer);
    }
  });

  globalThis.__aevrixProjectLoginSharedBufferV1 = true;
  return 'installed';
})()
""";
}
