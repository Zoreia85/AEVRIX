using Aevrix.Core;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class LoginFormDiscoveryJudgeTests
{
    [TestMethod]
    public void Evaluate_UniqueCoherentFormProducesRecipe()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#email", "login", "input", "email", 1, autoComplete: "username"),
                Element("#secret", "login", "input", "password", 2),
                Element("#submit", "login", "button", "submit", 3, visibleText: "Sign in")));

        Assert.AreEqual(LoginFormDiscoveryStatus.Ready, result.Status);
        Assert.IsNotNull(result.Recipe);
        Assert.AreEqual("#email", result.Recipe.UsernameSelector);
        Assert.AreEqual("#secret", result.Recipe.PasswordSelector);
        Assert.AreEqual("#submit", result.Recipe.SubmitSelector);
        Assert.AreEqual("https://example.com/login", result.Recipe.LoginUri.AbsoluteUri.TrimEnd('/'));
    }

    [TestMethod]
    public void Evaluate_MultipleVisiblePasswordFieldsIsAmbiguous()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#user", "login", "input", "text", 1),
                Element("#current", "login", "input", "password", 2),
                Element("#confirm", "login", "input", "password", 3),
                Element("#submit", "login", "button", "submit", 4)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Ambiguous, result.Status);
        Assert.AreEqual("multiple_password_fields", result.Code);
        CollectionAssert.AreEquivalent(new[] { "#current", "#confirm" }, result.CandidateSelectors.ToArray());
    }

    [TestMethod]
    public void Evaluate_TiedGenericUsernameCandidatesIsAmbiguous()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#first", "login", "input", "text", 1),
                Element("#second", "login", "input", "text", 2),
                Element("#secret", "login", "input", "password", 3),
                Element("#submit", "login", "button", "submit", 4)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Ambiguous, result.Status);
        Assert.AreEqual("username_field_ambiguous", result.Code);
        CollectionAssert.AreEquivalent(new[] { "#first", "#second" }, result.CandidateSelectors.ToArray());
    }

    [TestMethod]
    public void Evaluate_HiddenAndDisabledCandidatesAreIgnored()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#hidden", "login", "input", "email", 1, isVisible: false, autoComplete: "username"),
                Element("#disabled", "login", "input", "email", 2, isEnabled: false, autoComplete: "username"),
                Element("#live", "login", "input", "text", 3, name: "account"),
                Element("#secret", "login", "input", "password", 4),
                Element("#submit", "login", "button", "submit", 5)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Ready, result.Status);
        Assert.AreEqual("#live", result.Recipe!.UsernameSelector);
    }

    [TestMethod]
    public void Evaluate_ControlsFromOtherFormAreNotCombined()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#foreign-user", "newsletter", "input", "email", 1, autoComplete: "username"),
                Element("#local-user", "login", "input", "text", 2, name: "login"),
                Element("#secret", "login", "input", "password", 3),
                Element("#foreign-submit", "newsletter", "button", "submit", 4),
                Element("#local-submit", "login", "button", "submit", 5)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Ready, result.Status);
        Assert.AreEqual("#local-user", result.Recipe!.UsernameSelector);
        Assert.AreEqual("#local-submit", result.Recipe.SubmitSelector);
    }

    [TestMethod]
    public void Evaluate_TargetMismatchRejectsBeforeDiscovery()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-other",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#user", "login", "input", "text", 1),
                Element("#secret", "login", "input", "password", 2),
                Element("#submit", "login", "button", "submit", 3)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Rejected, result.Status);
        Assert.AreEqual("target_policy_mismatch", result.Code);
    }

    [TestMethod]
    public void Evaluate_PageOutsideAllowlistRejectsBeforeDiscovery()
    {
        var snapshot = Snapshot(
            Element("#user", "login", "input", "text", 1),
            Element("#secret", "login", "input", "password", 2),
            Element("#submit", "login", "button", "submit", 3)) with
        {
            PageUri = new Uri("https://other.example/login")
        };

        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            snapshot);

        Assert.AreEqual(LoginFormDiscoveryStatus.Rejected, result.Status);
        Assert.AreEqual("navigation_host_not_allowed", result.Code);
    }

    [TestMethod]
    public void Evaluate_DuplicateSelectorRejectsCorruptSnapshot()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#same", "login", "input", "text", 1),
                Element("#same", "login", "input", "password", 2),
                Element("#submit", "login", "button", "submit", 3)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Rejected, result.Status);
        Assert.AreEqual("snapshot_element_invalid", result.Code);
    }

    [TestMethod]
    public void Evaluate_OversizedSelectorRejectsCorruptSnapshot()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#" + new string('x', 512), "login", "input", "text", 1),
                Element("#secret", "login", "input", "password", 2),
                Element("#submit", "login", "button", "submit", 3)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Rejected, result.Status);
        Assert.AreEqual("snapshot_element_invalid", result.Code);
    }

    [TestMethod]
    public void Evaluate_MissingSubmitFailsClosed()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#user", "login", "input", "email", 1, autoComplete: "username"),
                Element("#secret", "login", "input", "password", 2)));

        Assert.AreEqual(LoginFormDiscoveryStatus.NotFound, result.Status);
        Assert.AreEqual("submit_control_not_found", result.Code);
    }

    [TestMethod]
    public void Evaluate_TiedSubmitControlsIsAmbiguous()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#user", "login", "input", "email", 1, autoComplete: "username"),
                Element("#secret", "login", "input", "password", 2),
                Element("#one", "login", "button", "submit", 3),
                Element("#two", "login", "button", "submit", 4)));

        Assert.AreEqual(LoginFormDiscoveryStatus.Ambiguous, result.Status);
        Assert.AreEqual("submit_control_ambiguous", result.Code);
    }

    [TestMethod]
    public void Evaluate_MissingTimestampIsRejected()
    {
        var result = LoginFormDiscoveryJudge.Evaluate(
            "target-web",
            Policy("target-web", "example.com"),
            Snapshot(
                Element("#user", "login", "input", "email", 1, autoComplete: "username"),
                Element("#secret", "login", "input", "password", 2),
                Element("#submit", "login", "button", "submit", 3)) with { ObservedAtUtc = default });

        Assert.AreEqual(LoginFormDiscoveryStatus.Rejected, result.Status);
        Assert.AreEqual("snapshot_timestamp_missing", result.Code);
    }

    private static LoginFormSnapshot Snapshot(params LoginDomElement[] elements) => new(
        PageUri: new Uri("https://example.com/login?return=%2Fapp#form"),
        Elements: elements,
        ObservedAtUtc: DateTimeOffset.UtcNow);

    private static LoginDomElement Element(
        string selector,
        string formKey,
        string tagName,
        string inputType,
        int order,
        string? name = null,
        string? id = null,
        string? autoComplete = null,
        string? ariaLabel = null,
        string? placeholder = null,
        string? visibleText = null,
        bool isVisible = true,
        bool isEnabled = true) => new(
            Selector: selector,
            FormKey: formKey,
            TagName: tagName,
            InputType: inputType,
            Name: name,
            Id: id,
            AutoComplete: autoComplete,
            AriaLabel: ariaLabel,
            Placeholder: placeholder,
            VisibleText: visibleText,
            IsVisible: isVisible,
            IsEnabled: isEnabled,
            DocumentOrder: order);

    private static ResearchBrowserPolicy Policy(string targetId, params string[] hosts) => new(
        TargetId: targetId,
        AllowedHosts: hosts,
        PersistTargetProfile: true,
        RememberCredentials: true,
        AutomaticRelogin: false,
        PauseImmediatelyOnLogout: true,
        ShortWindowFailureThreshold: 3,
        FailureWindow: TimeSpan.FromMinutes(15),
        Cooldown: TimeSpan.FromMinutes(10),
        ClearSiteDataWhenProjectDeleted: true,
        EgressPolicy: EgressPolicy.Offline());
}