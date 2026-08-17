namespace Aevrix.Core;

public enum ResearchBrowserNavigationStatus
{
    Allowed,
    Blocked
}

public sealed record ResearchBrowserNavigationDecision(
    ResearchBrowserNavigationStatus Status,
    string Code,
    string Detail)
{
    public bool Allowed => Status == ResearchBrowserNavigationStatus.Allowed;

    public static ResearchBrowserNavigationDecision Allow(Uri uri) => new(
        ResearchBrowserNavigationStatus.Allowed,
        "navigation_allowed",
        $"HTTPS navigation to {uri.Host} is inside the active project allowlist.");

    public static ResearchBrowserNavigationDecision Block(string code, string detail) => new(
        ResearchBrowserNavigationStatus.Blocked,
        code,
        detail);
}

/// <summary>
/// Pre-navigation boundary for the interactive Research Browser. This gate is intentionally stricter
/// than post-navigation session observations because it runs before WebView2 is allowed to leave the
/// currently governed target boundary.
/// </summary>
public static class ResearchBrowserNavigationGate
{
    public static ResearchBrowserNavigationDecision Evaluate(
        ResearchBrowserPolicy policy,
        Uri candidate)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            policy.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ResearchBrowserNavigationDecision.Block(
                "browser_policy_invalid",
                "The active Research Browser policy is invalid and navigation is blocked fail-closed.");
        }

        if (!candidate.IsAbsoluteUri)
        {
            return ResearchBrowserNavigationDecision.Block(
                "navigation_uri_not_absolute",
                "Research Browser navigation requires an absolute URI.");
        }

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ResearchBrowserNavigationDecision.Block(
                "navigation_requires_https",
                "Research Browser navigation is restricted to HTTPS.");
        }

        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            return ResearchBrowserNavigationDecision.Block(
                "navigation_embedded_credentials_forbidden",
                "Credentials must never be embedded in Research Browser URLs.");
        }

        if (!candidate.IsDefaultPort && candidate.Port != 443)
        {
            return ResearchBrowserNavigationDecision.Block(
                "navigation_non_default_https_port",
                "The current host allowlist does not grant authority to alternate HTTPS service ports.");
        }

        if (!policy.AllowedHosts.Any(host =>
                string.Equals(host, candidate.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return ResearchBrowserNavigationDecision.Block(
                "navigation_host_not_allowed",
                "The requested host is outside the active project's exact Research Browser allowlist.");
        }

        return ResearchBrowserNavigationDecision.Allow(candidate);
    }
}