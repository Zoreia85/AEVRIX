namespace Aevrix.Core;

/// <summary>
/// Fixed browser-side metadata collector for login-form discovery. The script intentionally never reads
/// input values, cookies, storage, raw HTML or form action payloads. Its output is untrusted metadata and
/// must pass LoginFormSnapshotParser and LoginFormDiscoveryJudge before it can become a LoginRecipe.
/// </summary>
public static class LoginFormDomSnapshotScript
{
    public const int SchemaVersion = 1;
    public const int MaxElements = 512;

    public const string Script = """
(() => {
  const schemaVersion = 1;
  const maxElements = 512;
  const maxText = 256;

  const clean = (candidate) => {
    if (candidate === null || candidate === undefined) return null;
    const text = String(candidate).replace(/[\u0000-\u001F\u007F]/g, ' ').trim();
    return text.length === 0 ? null : text.slice(0, maxText);
  };

  const escapeCss = (text) => {
    if (globalThis.CSS && typeof globalThis.CSS.escape === 'function') return globalThis.CSS.escape(text);
    return String(text).replace(/[^a-zA-Z0-9_-]/g, (ch) => `\\${ch.codePointAt(0).toString(16)} `);
  };

  const selectorFor = (element) => {
    if (element.id) return `#${escapeCss(element.id)}`;
    const parts = [];
    let current = element;
    while (current && current.nodeType === 1 && current !== document.documentElement) {
      const tag = String(current.localName || current.tagName || '').toLowerCase();
      if (!tag) break;
      let part = tag;
      const parent = current.parentElement;
      if (parent) {
        const siblings = Array.from(parent.children).filter((item) =>
          String(item.localName || item.tagName || '').toLowerCase() === tag);
        if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
      }
      parts.unshift(part);
      if (current.id) {
        parts[0] = `#${escapeCss(current.id)}`;
        break;
      }
      current = parent;
      if (parts.length >= 12) break;
    }
    return parts.join(' > ');
  };

  const formKeyFor = (element) => {
    const form = element.form || (typeof element.closest === 'function' ? element.closest('form') : null);
    return form ? selectorFor(form) : '__document__';
  };

  const isVisible = (element) => {
    if (element.hidden) return false;
    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return false;
    const style = globalThis.getComputedStyle(element);
    return style.display !== 'none' && style.visibility !== 'hidden' && style.visibility !== 'collapse';
  };

  const nodes = Array.from(document.querySelectorAll('input,button'));
  const elements = nodes.slice(0, maxElements).map((element, index) => {
    const tagName = String(element.localName || element.tagName || '').toLowerCase();
    const inputType = tagName === 'input' || tagName === 'button'
      ? String(element.getAttribute('type') || '').toLowerCase()
      : '';
    return {
      selector: selectorFor(element),
      formKey: formKeyFor(element),
      tagName,
      inputType,
      name: clean(element.getAttribute('name')),
      id: clean(element.getAttribute('id')),
      autoComplete: clean(element.getAttribute('autocomplete')),
      ariaLabel: clean(element.getAttribute('aria-label')),
      placeholder: clean(element.getAttribute('placeholder')),
      visibleText: tagName === 'button' ? clean(element.innerText || element.textContent) : null,
      isVisible: isVisible(element),
      isEnabled: !element.disabled && String(element.getAttribute('aria-disabled') || '').toLowerCase() !== 'true',
      documentOrder: index
    };
  });

  return JSON.stringify({
    schemaVersion,
    totalElementCount: nodes.length,
    elements
  });
})()
""";
}