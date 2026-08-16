using Aevrix.Core;

namespace AEVRIX.Desktop;

internal sealed record DesktopEvidenceProject(Guid Id, string Name, string Status);

internal sealed record DesktopEvidenceArtifact(
    StoredEvidenceArtifact Source,
    string EvidenceId,
    string Classification,
    string Kind,
    string DisplayName,
    string MediaType,
    long SizeBytes,
    DateTimeOffset StoredAt,
    string Sha256,
    string CaptureId,
    string? CaptureRelativePath)
{
    public bool IsQuarantine => Source.Classification == EvidenceClassification.Quarantine;
}

internal sealed record DesktopEvidenceCatalogState(
    bool Loaded,
    IReadOnlyList<DesktopEvidenceArtifact> Artifacts,
    string Detail);

internal sealed record DesktopEvidenceVerificationState(
    bool Completed,
    bool Verified,
    string Detail);

/// <summary>
/// Read-only Desktop adapter over ProjectRepository + EvidenceStore. It never opens or executes
/// evidence, and quarantine artifacts remain metadata-only in this surface.
/// </summary>
internal sealed class DesktopEvidenceExplorerService
{
    private readonly ProjectRepository _projects;
    private readonly EvidenceStore _evidence;

    public DesktopEvidenceExplorerService(AevrixDataPaths? paths = null)
    {
        var effectivePaths = paths ?? AevrixDataPaths.ForCurrentUser();
        _projects = new ProjectRepository(effectivePaths);
        _evidence = new EvidenceStore(effectivePaths, EvidenceMetadataRetention.Minimal);
    }

    public async Task<IReadOnlyList<DesktopEvidenceProject>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var envelopes = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
        return envelopes
            .Select(envelope => new DesktopEvidenceProject(
                envelope.Project.Id,
                envelope.Project.Name,
                envelope.Project.Status.ToString()))
            .ToArray();
    }

    public async Task<DesktopEvidenceCatalogState> LoadProjectAsync(
        Guid projectId,
        EvidenceClassification? classification,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            return new DesktopEvidenceCatalogState(
                false,
                Array.Empty<DesktopEvidenceArtifact>(),
                "Selecione um projeto válido antes de consultar evidências.");
        }

        try
        {
            var knownProjects = await _projects.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!knownProjects.Any(project => project.Project.Id == projectId))
            {
                return new DesktopEvidenceCatalogState(
                    false,
                    Array.Empty<DesktopEvidenceArtifact>(),
                    "O projeto selecionado não pertence ao catálogo local canônico atual.");
            }

            var artifacts = await _evidence.ReadIndexAsync(projectId, cancellationToken).ConfigureAwait(false);
            var filtered = artifacts
                .Where(artifact => classification is null || artifact.Classification == classification.Value)
                .OrderByDescending(artifact => artifact.StoredAt)
                .Select(artifact => new DesktopEvidenceArtifact(
                    artifact,
                    artifact.EvidenceId,
                    artifact.Classification.ToString(),
                    artifact.Kind,
                    artifact.OriginalName,
                    artifact.MediaType,
                    artifact.SizeBytes,
                    artifact.StoredAt,
                    artifact.Sha256,
                    artifact.CaptureId,
                    artifact.CaptureRelativePath))
                .ToArray();

            var quarantined = filtered.Count(artifact => artifact.IsQuarantine);
            var detail = filtered.Length == 0
                ? "Nenhuma evidência corresponde ao filtro atual."
                : quarantined == 0
                    ? $"{filtered.Length} evidência(s) indexada(s) carregada(s). Nenhuma entrada de quarentena neste filtro."
                    : $"{filtered.Length} evidência(s) indexada(s) carregada(s); {quarantined} em quarentena (metadados somente).";

            return new DesktopEvidenceCatalogState(true, filtered, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return new DesktopEvidenceCatalogState(
                false,
                Array.Empty<DesktopEvidenceArtifact>(),
                $"O índice de evidências foi rejeitado ({ex.GetType().Name}). Nenhum artefato foi inferido.");
        }
    }

    public async Task<DesktopEvidenceVerificationState> VerifyAsync(
        Guid expectedProjectId,
        DesktopEvidenceArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        if (expectedProjectId == Guid.Empty || artifact.Source.ProjectId != expectedProjectId)
        {
            return new DesktopEvidenceVerificationState(
                true,
                false,
                "Verificação bloqueada: o artefato não pertence ao projeto selecionado.");
        }

        try
        {
            var verified = await _evidence.VerifyAsync(expectedProjectId, artifact.Source, cancellationToken)
                .ConfigureAwait(false);
            return verified
                ? new DesktopEvidenceVerificationState(
                    true,
                    true,
                    $"SHA-256 verificado para {artifact.EvidenceId}. O conteúdo não foi aberto nem executado.")
                : new DesktopEvidenceVerificationState(
                    true,
                    false,
                    $"Falha de integridade para {artifact.EvidenceId}: arquivo ausente, fora do projeto ou SHA-256 divergente.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return new DesktopEvidenceVerificationState(
                false,
                false,
                $"A verificação não pôde ser concluída com segurança ({ex.GetType().Name}).");
        }
    }
}
