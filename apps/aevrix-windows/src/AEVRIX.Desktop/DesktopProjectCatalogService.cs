using Aevrix.Core;

namespace AEVRIX.Desktop;

internal sealed record DesktopProjectSummary(
    Guid Id,
    string Name,
    string TargetId,
    string Domain,
    string Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastActivityAt,
    long SanitizedBytes,
    long QuarantineBytes)
{
    public bool RequiresAttention => QuarantineBytes > 0 || string.Equals(Status, "Blocked", StringComparison.OrdinalIgnoreCase) || string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);

    public DateTimeOffset EffectiveActivityAt => LastActivityAt ?? UpdatedAt;
}

internal sealed record DesktopProjectCatalogState(
    bool Loaded,
    IReadOnlyList<DesktopProjectSummary> Projects,
    string Detail);

/// <summary>
/// Read-only Desktop adapter over the canonical ProjectRepository. The Desktop does not invent
/// a parallel project store and does not mutate authorization, browser policy, or mission state.
/// </summary>
internal sealed class DesktopProjectCatalogService
{
    private readonly ProjectRepository _repository;

    public DesktopProjectCatalogService(AevrixDataPaths? paths = null)
    {
        _repository = new ProjectRepository(paths ?? AevrixDataPaths.ForCurrentUser());
    }

    public async Task<DesktopProjectCatalogState> ListAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var envelopes = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
            var projects = envelopes
                .Select(envelope => new DesktopProjectSummary(
                    envelope.Project.Id,
                    envelope.Project.Name,
                    envelope.Project.TargetId,
                    envelope.Project.Domain.ToString(),
                    envelope.Project.Status.ToString(),
                    envelope.UpdatedAt,
                    envelope.Project.LastActivityAt,
                    envelope.Project.SanitizedBytes,
                    envelope.Project.QuarantineBytes))
                .OrderByDescending(project => project.EffectiveActivityAt)
                .ToArray();

            var attentionCount = projects.Count(project => project.RequiresAttention);
            var detail = projects.Length == 0
                ? "Nenhum projeto local válido foi encontrado nesta estação."
                : attentionCount == 0
                    ? $"{projects.Length} projeto(s) local(is) carregado(s); nenhum sinal local de quarentena/bloqueio."
                    : $"{projects.Length} projeto(s) local(is) carregado(s); {attentionCount} exige(m) atenção por quarentena, bloqueio ou falha.";

            return new DesktopProjectCatalogState(true, projects, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return new DesktopProjectCatalogState(
                false,
                Array.Empty<DesktopProjectSummary>(),
                $"O catálogo local não pôde ser lido com segurança ({ex.GetType().Name}). Nenhum projeto foi inferido.");
        }
    }
}
