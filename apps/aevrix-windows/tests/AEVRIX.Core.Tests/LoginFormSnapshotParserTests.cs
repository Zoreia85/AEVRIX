using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class LoginFormSnapshotParserTests
{
    [TestMethod]
    public void CollectorScript_DoesNotReadSensitiveBrowserStateOrInputValues()
    {
        var script = LoginFormDomSnapshotScript.Script;
        Assert.IsFalse(script.Contains(".value", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("getAttribute('value", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("cookie", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("localStorage", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("sessionStorage", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("outerHTML", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("innerHTML", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(script, "querySelectorAll('input,button')");
    }

    [TestMethod]
    public void Parse_ValidSchemaProducesSnapshotWithoutValues()
    {
        var snapshot = LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), ValidPayload(), DateTimeOffset.UtcNow);
        Assert.AreEqual(3, snapshot.Elements.Count);
        Assert.AreEqual("#user", snapshot.Elements[0].Selector);
        Assert.AreEqual("password", snapshot.Elements[1].InputType);
        Assert.AreEqual("Sign in", snapshot.Elements[2].VisibleText);
    }

    [TestMethod]
    public void Parse_UnknownValuePropertyIsRejected()
    {
        var payload = ValidPayload().Replace("\"name\":\"account\"", "\"name\":\"account\",\"value\":\"forbidden\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Parse_UnknownRawHtmlPropertyIsRejected()
    {
        var payload = ValidPayload().Replace("\"name\":\"account\"", "\"name\":\"account\",\"outerHtml\":\"forbidden\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Parse_TruncatedElementSetIsRejected()
    {
        var payload = ValidPayload().Replace("\"totalElementCount\":3", "\"totalElementCount\":513", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Parse_ElementCountMismatchIsRejected()
    {
        var payload = ValidPayload().Replace("\"totalElementCount\":3", "\"totalElementCount\":2", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Parse_DuplicateRootPropertyIsRejected()
    {
        var payload = ValidPayload().Replace("{\"schemaVersion\":1,", "{\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Parse_DuplicateDocumentOrderIsRejected()
    {
        var payload = ValidPayload().Replace("\"documentOrder\":1", "\"documentOrder\":0", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Parse_OversizedPayloadIsRejectedBeforeJsonParsing()
    {
        var payload = new string('x', 512 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => LoginFormSnapshotParser.Parse(new Uri("https://example.com/login"), payload, DateTimeOffset.UtcNow));
    }

    private static string ValidPayload() =>
        """
        {"schemaVersion":1,"totalElementCount":3,"elements":[
          {"selector":"#user","formKey":"#login","tagName":"input","inputType":"email","name":"account","id":"user","autoComplete":"username","ariaLabel":null,"placeholder":"Email","visibleText":null,"isVisible":true,"isEnabled":true,"documentOrder":0},
          {"selector":"#secret","formKey":"#login","tagName":"input","inputType":"password","name":null,"id":"secret","autoComplete":"current-password","ariaLabel":null,"placeholder":null,"visibleText":null,"isVisible":true,"isEnabled":true,"documentOrder":1},
          {"selector":"#submit","formKey":"#login","tagName":"button","inputType":"submit","name":null,"id":"submit","autoComplete":null,"ariaLabel":null,"placeholder":null,"visibleText":"Sign in","isVisible":true,"isEnabled":true,"documentOrder":2}
        ]}
        """;
}