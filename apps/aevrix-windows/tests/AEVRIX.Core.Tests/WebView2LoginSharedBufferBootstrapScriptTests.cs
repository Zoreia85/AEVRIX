using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class WebView2LoginSharedBufferBootstrapScriptTests
{
    [TestMethod]
    public void Script_UsesSharedBufferAndReleasesItBeforeAcknowledgement()
    {
        var script = WebView2LoginSharedBufferBootstrapScript.Script;
        StringAssert.Contains(script, "sharedbufferreceived");
        StringAssert.Contains(script, "event.getBuffer()");
        StringAssert.Contains(script, "chrome.webview.releaseBuffer(buffer)");
        StringAssert.Contains(script, "chrome.webview.postMessage");
        Assert.IsTrue(
            script.IndexOf("releaseBuffer(buffer)", StringComparison.Ordinal)
            < script.LastIndexOf("postResult(data.nonce", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Script_DoesNotReadCookiesStorageOrRawHtml()
    {
        var script = WebView2LoginSharedBufferBootstrapScript.Script;
        Assert.IsFalse(script.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("localStorage", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("sessionStorage", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("outerHTML", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("innerHTML", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("console.", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Script_RequiresUniqueSelectorsAndPasswordInputType()
    {
        var script = WebView2LoginSharedBufferBootstrapScript.Script;
        StringAssert.Contains(script, "nodes.length === 1");
        StringAssert.Contains(script, "secretInput.type");
        StringAssert.Contains(script, "password_selector_invalid");
        StringAssert.Contains(script, "username_selector_invalid");
        StringAssert.Contains(script, "submit_selector_invalid");
    }

    [TestMethod]
    public void Script_ValidatesPacketMagicVersionAndExactLengths()
    {
        var script = WebView2LoginSharedBufferBootstrapScript.Script;
        StringAssert.Contains(script, "bytes[0] !== 0x41");
        StringAssert.Contains(script, "bytes[1] !== 0x58");
        StringAssert.Contains(script, "bytes[2] !== 0x4c");
        StringAssert.Contains(script, "bytes[3] !== 0x47");
        StringAssert.Contains(script, "bytes[4] !== 1");
        StringAssert.Contains(script, "13 + userLength + secretLength !== bytes.length");
    }
}