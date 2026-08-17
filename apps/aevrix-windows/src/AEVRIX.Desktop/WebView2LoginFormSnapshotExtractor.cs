using System.Text.Json;
using Aevrix.Core;
using Microsoft.Web.WebView2.Core;

namespace AEVRIX.Desktop;

public sealed class WebView2LoginFormSnapshotExtractor
{
    private const int MaxEncodedResultChars = 1024 * 1024;
    private readonly TimeProvider _timeProvider;

    public WebView2LoginFormSnapshotExtractor(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LoginFormSnapshot> CaptureAsync(
        CoreWebView2 core,
        string targetId,
        ResearchBrowserPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(targetId, policy.TargetId, StringComparison.Ordinal))
            throw new InvalidOperationException("DOM snapshot target does not match the active browser policy.");
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var pageUri))
            throw new InvalidOperationException("Research Browser has no absolute current page URI.");

        var navigation = ResearchBrowserNavigationGate.Evaluate(policy, pageUri);
        if (!navigation.Allowed)
            throw new InvalidOperationException($"DOM snapshot is blocked by browser policy: {navigation.Code}.");

        var encoded = await core.ExecuteScriptAsync(LoginFormDomSnapshotScript.Script);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedResultChars)
            throw new InvalidDataException("WebView2 login DOM snapshot result is empty or oversized.");

        string payload;
        try
        {
            payload = JsonSerializer.Deserialize<string>(encoded)
                ?? throw new InvalidDataException("WebView2 login DOM snapshot result did not contain a JSON string.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("WebView2 login DOM snapshot result envelope is invalid.", ex);
        }
        return LoginFormSnapshotParser.Parse(pageUri, payload, _timeProvider.GetUtcNow());
    }
}