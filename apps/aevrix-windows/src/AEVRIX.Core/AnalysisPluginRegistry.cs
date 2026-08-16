namespace Aevrix.Core;

public sealed record AnalysisPluginSelection(
    AnalysisPluginDescriptor Plugin,
    int Priority,
    int SpecificityScore);

/// <summary>
/// Resolves analysis requests to a single declared plugin without retaining request or evidence data.
/// Selection is fail-closed: unsupported, policy-ineligible, or equally-ranked candidates are rejected.
/// </summary>
public sealed class AnalysisPluginRegistry
{
    private readonly IReadOnlyList<Registration> _registrations;

    public AnalysisPluginRegistry(IEnumerable<AnalysisPluginDescriptor> plugins)
        : this(plugins.Select(plugin => (plugin, Priority: 0)))
    {
    }

    public AnalysisPluginRegistry(IEnumerable<(AnalysisPluginDescriptor Plugin, int Priority)> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);

        var registrations = plugins
            .Select(item => new Registration(item.Plugin, item.Priority))
            .ToArray();

        if (registrations.Length == 0)
        {
            throw new ArgumentException("At least one analysis plugin must be registered.", nameof(plugins));
        }

        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration.Plugin);
            registration.Plugin.Validate();
        }

        var duplicate = registrations
            .GroupBy(
                item => $"{item.Plugin.PluginId}\n{item.Plugin.Version}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException("Duplicate plugin id/version registrations are not allowed.", nameof(plugins));
        }

        _registrations = registrations;
    }

    public AnalysisPluginSelection Resolve(AnalysisExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(request.Target);
        request.Scope.Validate();
        request.Target.Validate();
        WorkspaceScope.ValidateToken(request.RequestId, nameof(request.RequestId));

        var candidates = new List<Candidate>();
        var policyRejected = false;

        foreach (var registration in _registrations)
        {
            if (!registration.Plugin.Supports(request.Target, request.Technique))
            {
                continue;
            }

            try
            {
                request.ValidateAgainst(registration.Plugin);
            }
            catch (InvalidOperationException)
            {
                policyRejected = true;
                continue;
            }

            candidates.Add(new Candidate(
                registration.Plugin,
                registration.Priority,
                ComputeSpecificity(registration.Plugin, request.Target)));
        }

        if (candidates.Count == 0)
        {
            if (policyRejected)
            {
                throw new InvalidOperationException("No registered plugin is eligible under the execution policy for this request.");
            }

            throw new InvalidOperationException("No registered plugin supports the requested target and analysis technique.");
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.SpecificityScore)
            .ThenBy(candidate => candidate.Plugin.PluginId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Plugin.Version, StringComparer.Ordinal)
            .ToArray();

        var winner = ordered[0];
        if (ordered.Length > 1
            && ordered[1].Priority == winner.Priority
            && ordered[1].SpecificityScore == winner.SpecificityScore)
        {
            throw new InvalidOperationException(
                "Analysis plugin resolution is ambiguous; adjust capability specificity or explicit priority before execution.");
        }

        return new AnalysisPluginSelection(winner.Plugin, winner.Priority, winner.SpecificityScore);
    }

    private static int ComputeSpecificity(AnalysisPluginDescriptor plugin, AnalysisTarget target)
    {
        var score = 0;
        score += IsExact(plugin.Domains, target.Domain) ? 8 : 0;
        score += IsExact(plugin.Systems, target.System) ? 8 : 0;
        score += target.Languages.Count(language => IsExact(plugin.Languages, language)) * 4;
        score += target.Formats.Count(format => IsExact(plugin.Formats, format)) * 4;
        return score;
    }

    private static bool IsExact(IReadOnlyCollection<string> supported, string requested) =>
        supported.Contains(requested, StringComparer.OrdinalIgnoreCase)
        && !supported.Contains("*", StringComparer.OrdinalIgnoreCase);

    private sealed record Registration(AnalysisPluginDescriptor Plugin, int Priority);
    private sealed record Candidate(AnalysisPluginDescriptor Plugin, int Priority, int SpecificityScore);
}
