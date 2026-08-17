namespace Aevrix.Core;

/// <summary>
/// Fixed renderer bootstrap for one-time login credential packets posted through WebView2 shared memory.
/// The host supplies only non-secret selectors/nonce as additionalData JSON; credential bytes arrive only
/// through a read-only shared buffer. The renderer releases the buffer before acknowledging completion.
/// </summary>
public static class WebView2LoginSharedBufferBootstrapScript
{
    public const string ResultMessageType = "aevrix-login-shared-buffer-result";
    public const string RequestKind = "aevrix-login-shared-buffer-v1";

    public const string Script = """
(() => {
  if (globalThis.__aevrixLoginSharedBufferV1 === true) return true;
  globalThis.__aevrixLoginSharedBufferV1 = true;

  const resultType = 'aevrix-login-shared-buffer-result';
  const requestKind = 'aevrix-login-shared-buffer-v1';

  const postResult = (nonce, ok, code) => {
    chrome.webview.postMessage({ type: resultType, nonce, ok, code });
  };

  const unique = (selector) => {
    if (typeof selector !== 'string' || selector.length === 0 || selector.length > 512) return null;
    const nodes = document.querySelectorAll(selector);
    return nodes.length === 1 ? nodes[0] : null;
  };

  const setInput = (element, text) => {
    if (!(element instanceof HTMLInputElement)) return false;
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    if (typeof setter !== 'function') return false;
    setter.call(element, text);
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  };

  chrome.webview.addEventListener('sharedbufferreceived', (event) => {
    const data = event.additionalData;
    if (!data || data.kind !== requestKind || typeof data.nonce !== 'string') return;

    let buffer = null;
    let userText = '';
    let secretText = '';
    let ok = false;
    let code = 'renderer_rejected';

    try {
      buffer = event.getBuffer();
      const bytes = new Uint8Array(buffer);
      if (bytes.length < 13
          || bytes[0] !== 0x41 || bytes[1] !== 0x58 || bytes[2] !== 0x4c || bytes[3] !== 0x47
          || bytes[4] !== 1) {
        code = 'packet_header_invalid';
        return;
      }

      const view = new DataView(buffer);
      const userLength = view.getUint32(5, true);
      const secretLength = view.getUint32(9, true);
      if (13 + userLength + secretLength !== bytes.length || userLength === 0 || secretLength === 0) {
        code = 'packet_length_invalid';
        return;
      }

      const decoder = new TextDecoder('utf-8', { fatal: true });
      userText = decoder.decode(bytes.subarray(13, 13 + userLength));
      secretText = decoder.decode(bytes.subarray(13 + userLength));

      const userInput = unique(data.usernameSelector);
      const secretInput = unique(data.passwordSelector);
      const submit = unique(data.submitSelector);
      if (!(userInput instanceof HTMLInputElement)) {
        code = 'username_selector_invalid';
        return;
      }
      if (!(secretInput instanceof HTMLInputElement) || String(secretInput.type).toLowerCase() !== 'password') {
        code = 'password_selector_invalid';
        return;
      }
      if (!submit) {
        code = 'submit_selector_invalid';
        return;
      }
      if (!setInput(userInput, userText) || !setInput(secretInput, secretText)) {
        code = 'input_assignment_failed';
        return;
      }

      submit.click();
      ok = true;
      code = 'submitted';
    } catch {
      ok = false;
      code = 'renderer_exception';
    } finally {
      userText = '';
      secretText = '';
      if (buffer !== null) chrome.webview.releaseBuffer(buffer);
      postResult(data.nonce, ok, code);
    }
  });

  return true;
})()
""";
}