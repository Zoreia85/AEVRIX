namespace Aevrix.Core;

public sealed record LoginDomElement(
    string Selector,
    string FormKey,
    string TagName,
    string InputType,
    string? Name,
    string? Id,
    string? AutoComplete,
    string? AriaLabel,
    string? Placeholder,
    string? VisibleText,
    bool IsVisible,
    bool IsEnabled,
    int DocumentOrder);

public sealed record LoginFormSnapshot(
    Uri PageUri,
    IReadOnlyList<LoginDomElement> Elements,
    DateTimeOffset ObservedAtUtc);

public enum LoginFormDiscoveryStatus
{
    NotFound,
    Ready,
    Ambiguous,
    Rejected
}

public sealed record LoginFormDiscoveryResult(
    LoginFormDiscoveryStatus Status,
    string Code,
    LoginRecipe? Recipe,
    IReadOnlyList<string> CandidateSelectors)
{
    public static LoginFormDiscoveryResult NotFound(string code) =>
        new(LoginFormDiscoveryStatus.NotFound, code, null, Array.Empty<string>());

    public static LoginFormDiscoveryResult Ready(LoginRecipe recipe) =>
        new(LoginFormDiscoveryStatus.Ready, "login_form_ready", recipe, Array.Empty<string>());

    public static LoginFormDiscoveryResult Ambiguous(string code, IEnumerable<LoginDomElement> candidates) =>
        new(
            LoginFormDiscoveryStatus.Ambiguous,
            code,
            null,
            candidates.Select(item => item.Selector).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    public static LoginFormDiscoveryResult Rejected(string code) =>
        new(LoginFormDiscoveryStatus.Rejected, code, null, Array.Empty<string>());
}

/// <summary>
/// Conservative Core-side judge for DOM metadata captured by a browser adapter. It never consumes field values.
/// Discovery succeeds only when one coherent password, username and submit control can be selected without a tie.
/// Ambiguous pages are intentionally left for explicit user confirmation or a future governed learning workflow.
/// </summary>
public static class LoginFormDiscoveryJudge
{
    private const int MaxElements = 512;
    private const int MaxSelectorLength = 512;
    private const int MaxFormKeyLength = 160;
    private const int MaxMetadataLength = 256;

    public static LoginFormDiscoveryResult Evaluate(
        string targetId,
        ResearchBrowserPolicy policy,
        LoginFormSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return LoginFormDiscoveryResult.Rejected("target_id_missing");
        }
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.PageUri);
        ArgumentNullException.ThrowIfNull(snapshot.Elements);

        try
        {
            policy.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return LoginFormDiscoveryResult.Rejected("browser_policy_invalid");
        }

        if (!string.Equals(targetId, policy.TargetId, StringComparison.Ordinal))
        {
            return LoginFormDiscoveryResult.Rejected("target_policy_mismatch");
        }

        var navigation = ResearchBrowserNavigationGate.Evaluate(policy, snapshot.PageUri);
        if (!navigation.Allowed)
        {
            return LoginFormDiscoveryResult.Rejected(navigation.Code);
        }

        if (snapshot.ObservedAtUtc == default)
        {
            return LoginFormDiscoveryResult.Rejected("snapshot_timestamp_missing");
        }

        if (snapshot.Elements.Count > MaxElements)
        {
            return LoginFormDiscoveryResult.Rejected("snapshot_element_limit_exceeded");
        }

        var selectorSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in snapshot.Elements)
        {
            if (!ValidateElement(element) || !selectorSet.Add(element.Selector))
            {
                return LoginFormDiscoveryResult.Rejected("snapshot_element_invalid");
            }
        }

        var active = snapshot.Elements
            .Where(item => item.IsVisible && item.IsEnabled)
            .ToArray();

        var secretFields = active
            .Where(IsPasswordInput)
            .OrderBy(item => item.DocumentOrder)
            .ToArray();
        if (secretFields.Length == 0)
        {
            return LoginFormDiscoveryResult.NotFound("password_field_not_found");
        }
        if (secretFields.Length != 1)
        {
            return LoginFormDiscoveryResult.Ambiguous("multiple_password_fields", secretFields);
        }

        var secretField = secretFields[0];
        var formKey = secretField.FormKey;

        var userCandidates = active
            .Where(item => string.Equals(item.FormKey, formKey, StringComparison.Ordinal))
            .Where(item => item.DocumentOrder < secretField.DocumentOrder)
            .Where(IsUserInputCandidate)
            .Select(item => new ScoredElement(item, ScoreUserCandidate(item)))
            .Where(item => item.Score > 0)
            .ToArray();

        var userSelection = SelectUniqueTop(userCandidates);
        if (userSelection.Status == SelectionStatus.None)
        {
            return LoginFormDiscoveryResult.NotFound("username_field_not_found");
        }
        if (userSelection.Status == SelectionStatus.Ambiguous)
        {
            return LoginFormDiscoveryResult.Ambiguous(
                "username_field_ambiguous",
                userSelection.Candidates.Select(item => item.Element));
        }

        var submitCandidates = active
            .Where(item => string.Equals(item.FormKey, formKey, StringComparison.Ordinal))
            .Where(IsSubmitCandidate)
            .Select(item => new ScoredElement(item, ScoreSubmitCandidate(item)))
            .Where(item => item.Score > 0)
            .ToArray();

        var submitSelection = SelectUniqueTop(submitCandidates);
        if (submitSelection.Status == SelectionStatus.None)
        {
            return LoginFormDiscoveryResult.NotFound("submit_control_not_found");
        }
        if (submitSelection.Status == SelectionStatus.Ambiguous)
        {
            return LoginFormDiscoveryResult.Ambiguous(
                "submit_control_ambiguous",
                submitSelection.Candidates.Select(item => item.Element));
        }

        var canonicalLoginUri = ProjectCredentialVault.CanonicalizeLoginUri(snapshot.PageUri);
        var recipe = new LoginRecipe(
            TargetId: targetId,
            LoginUri: new Uri(canonicalLoginUri, UriKind.Absolute),
            UsernameSelector: userSelection.Selected!.Selector,
            PasswordSelector: secretField.Selector,
            SubmitSelector: submitSelection.Selected!.Selector,
            AuthenticatedUrlMarkers: Array.Empty<string>(),
            AuthenticatedTextMarkers: Array.Empty<string>(),
            LoggedOutUrlMarkers: Array.Empty<string>(),
            LoggedOutTextMarkers: Array.Empty<string>(),
            LearnedAt: snapshot.ObservedAtUtc).Validate();

        return LoginFormDiscoveryResult.Ready(recipe);
    }

    private static bool ValidateElement(LoginDomElement element)
    {
        if (element is null
            || string.IsNullOrWhiteSpace(element.Selector)
            || element.Selector.Length > MaxSelectorLength
            || element.Selector.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(element.FormKey)
            || element.FormKey.Length > MaxFormKeyLength
            || element.FormKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(element.TagName)
            || element.TagName.Length > 32
            || element.TagName.Any(char.IsControl)
            || element.InputType.Length > 32
            || element.InputType.Any(char.IsControl)
            || element.DocumentOrder < 0)
        {
            return false;
        }

        return MetadataValid(element.Name)
            && MetadataValid(element.Id)
            && MetadataValid(element.AutoComplete)
            && MetadataValid(element.AriaLabel)
            && MetadataValid(element.Placeholder)
            && MetadataValid(element.VisibleText);
    }

    private static bool MetadataValid(string? value) =>
        value is null || (value.Length <= MaxMetadataLength && !value.Any(char.IsControl));

    private static bool IsPasswordInput(LoginDomElement element) =>
        string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase)
        && string.Equals(element.InputType, "password", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserInputCandidate(LoginDomElement element)
    {
        if (!string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return element.InputType.Length == 0
            || string.Equals(element.InputType, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(element.InputType, "email", StringComparison.OrdinalIgnoreCase)
            || string.Equals(element.InputType, "tel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubmitCandidate(LoginDomElement element)
    {
        if (string.Equals(element.TagName, "button", StringComparison.OrdinalIgnoreCase))
        {
            return element.InputType.Length == 0
                || string.Equals(element.InputType, "submit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.InputType, "button", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(element.InputType, "submit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(element.InputType, "button", StringComparison.OrdinalIgnoreCase));
    }

    private static int ScoreUserCandidate(LoginDomElement element)
    {
        var autoComplete = Normalize(element.AutoComplete);
        if (string.Equals(autoComplete, "username", StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }
        if (string.Equals(autoComplete, "email", StringComparison.OrdinalIgnoreCase))
        {
            return 900;
        }
        if (string.Equals(element.InputType, "email", StringComparison.OrdinalIgnoreCase))
        {
            return 850;
        }

        var descriptor = Descriptor(element);
        if (ContainsAny(descriptor, "email", "e-mail"))
        {
            return 700;
        }
        if (ContainsAny(descriptor, "username", "user", "login", "account"))
        {
            return 600;
        }

        return string.Equals(element.InputType, "text", StringComparison.OrdinalIgnoreCase)
            || element.InputType.Length == 0
            ? 100
            : 50;
    }

    private static int ScoreSubmitCandidate(LoginDomElement element)
    {
        var score = 0;
        if (string.Equals(element.InputType, "submit", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }
        else if (string.Equals(element.TagName, "button", StringComparison.OrdinalIgnoreCase))
        {
            score += 700;
        }
        else if (string.Equals(element.InputType, "button", StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        var descriptor = Descriptor(element);
        if (ContainsAny(descriptor, "sign in", "signin", "log in", "login", "continue", "next", "entrar", "acessar"))
        {
            score += 100;
        }
        return score;
    }

    private static Selection SelectUniqueTop(IReadOnlyList<ScoredElement> candidates)
    {
        if (candidates.Count == 0)
        {
            return new Selection(SelectionStatus.None, null, Array.Empty<ScoredElement>());
        }

        var topScore = candidates.Max(item => item.Score);
        var top = candidates
            .Where(item => item.Score == topScore)
            .OrderBy(item => item.Element.DocumentOrder)
            .ToArray();
        if (top.Length != 1)
        {
            return new Selection(SelectionStatus.Ambiguous, null, top);
        }
        return new Selection(SelectionStatus.Selected, top[0].Element, top);
    }

    private static string Descriptor(LoginDomElement element) => string.Join(
        ' ',
        new[]
        {
            element.Name,
            element.Id,
            element.AutoComplete,
            element.AriaLabel,
            element.Placeholder,
            element.VisibleText
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private sealed record ScoredElement(LoginDomElement Element, int Score);

    private enum SelectionStatus
    {
        None,
        Selected,
        Ambiguous
    }

    private sealed record Selection(
        SelectionStatus Status,
        LoginDomElement? Selected,
        IReadOnlyList<ScoredElement> Candidates);
}