namespace Aevrix.Core;

public sealed record BlueprintCommandResultData(
    string CaptureId,
    string BlueprintSha256,
    string ProjectRelativeExportRoot,
    int EvidenceCount,
    int ArchitectureElementCount,
    int WorkflowCount,
    int ApiEndpointCount,
    int UiComponentCount,
    int BehavioralModelCount,
    double ReadinessPercent,
    string ReadinessGrade,
    bool ReadyForIndependentRebuild,
    int GeneratedFileCount);

/// <summary>
/// Converts the versioned EngineProtocol GenerateBlueprintCommand into the fail-closed
/// Evidence -> Project Blueprint synthesis operation. The handler lives in Core so the
/// EngineHost remains a thin IPC/process boundary and does not duplicate synthesis rules.
/// </summary>
public sealed class BlueprintCommandHandler
{
    private readonly AevrixDataPaths _paths;
    private readonly ProjectRepository _projects;
    private readonly ProjectBlueprintSynthesisService _synthesis;
    private readonly string _researchRuntimeRoot;

    public BlueprintCommandHandler(
        AevrixDataPaths paths,
        string? researchRuntimeRoot = null,
        ProjectRepository? projects = null,
        ProjectBlueprintSynthesisService? synthesis = null)
    {
        _paths = paths.EnsureCreated();
        _projects = projects ?? new ProjectRepository(_paths);
        _synthesis = synthesis ?? new ProjectBlueprintSynthesisService(_paths, projects: _projects);
        _researchRuntimeRoot = Path.GetFullPath(researchRuntimeRoot ?? DefaultResearchRuntimeRoot());
    }

    public async Task<EngineResponse> HandleAsync(
        GenerateBlueprintCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!Guid.TryParse(command.ProjectId, out var projectId) || projectId == Guid.Empty)
        {
            return Failure(command.RequestId, "invalid_project_id", "Project id must be a non-empty GUID.");
        }
        if (!EngineProtocol.IsSafeCaptureId(command.CaptureId))
        {
            return Failure(command.RequestId, "invalid_capture_id", "Capture id is invalid.");
        }
        if (command.IncludeRawEvidenceReferences)
        {
            return Failure(
                command.RequestId,
                "blueprint_policy_blocked",
                "Raw/quarantine evidence references are blocked by the default Blueprint policy.");
        }

        ProjectEnvelope envelope;
        try
        {
            envelope = await _projects.LoadAsync(projectId, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Failure(command.RequestId, "project_not_found", "The requested AEVRIX project was not found.");
        }
        catch (InvalidDataException)
        {
            return Failure(command.RequestId, "project_invalid", "The project manifest failed validation.");
        }

        if (envelope.Project.Domain is not ProjectDomain.Web)
        {
            return Failure(
                command.RequestId,
                "blueprint_domain_pending",
                $"Evidence-to-Blueprint synthesis is not yet promoted for {envelope.Project.Domain} projects.");
        }

        string captureRoot;
        try
        {
            captureRoot = ResolveSanitizedCaptureRoot(envelope.Project.TargetId, command.CaptureId);
        }
        catch (ArgumentException)
        {
            return Failure(command.RequestId, "invalid_target_id", "Project target id is invalid.");
        }
        catch (InvalidDataException)
        {
            return Failure(command.RequestId, "capture_path_rejected", "The capture path failed containment validation.");
        }

        try
        {
            var result = await _synthesis.SynthesizeCaptureAsync(
                projectId,
                command.CaptureId,
                captureRoot,
                includeRawEvidenceReferences: false,
                cancellationToken);

            var projectRoot = Path.GetFullPath(_paths.ProjectRoot(projectId));
            var exportRoot = Path.GetFullPath(result.Export.RootPath);
            var relativeExport = Path.GetRelativePath(projectRoot, exportRoot).Replace('\\', '/');
            if (relativeExport == ".."
                || relativeExport.StartsWith("../", StringComparison.Ordinal)
                || Path.IsPathRooted(relativeExport))
            {
                return Failure(command.RequestId, "blueprint_export_path_rejected", "Blueprint export escaped the project root.");
            }

            var data = new BlueprintCommandResultData(
                result.CaptureId,
                result.Export.BlueprintSha256,
                relativeExport,
                result.Blueprint.Evidence.Count,
                result.Blueprint.ArchitectureElements.Count,
                result.Blueprint.Workflows.Count,
                result.Blueprint.ApiEndpoints.Count,
                result.Blueprint.UiComponents.Count,
                result.Blueprint.BehavioralModels.Count,
                result.Blueprint.Readiness.OverallPercent,
                result.Blueprint.Readiness.Grade,
                result.Blueprint.Readiness.ReadyForIndependentRebuild,
                result.Export.GeneratedFiles.Count);

            return new EngineResponse(
                command.RequestId,
                true,
                "blueprint_generated",
                "Project Blueprint was generated from verified sanitized capture evidence.",
                data);
        }
        catch (DirectoryNotFoundException)
        {
            return Failure(command.RequestId, "capture_not_found", "Sanitized capture evidence was not found.");
        }
        catch (FileNotFoundException)
        {
            return Failure(command.RequestId, "capture_incomplete", "The capture is missing a required manifest or artifact.");
        }
        catch (InvalidDataException)
        {
            return Failure(command.RequestId, "blueprint_integrity_failed", "Capture or Evidence Store integrity validation failed.");
        }
        catch (NotSupportedException)
        {
            return Failure(command.RequestId, "blueprint_domain_pending", "This capture domain is not yet promoted for Blueprint synthesis.");
        }
        catch (InvalidOperationException)
        {
            return Failure(command.RequestId, "blueprint_validation_failed", "The synthesized Blueprint failed a policy or model validation gate.");
        }
        catch (System.Text.Json.JsonException)
        {
            return Failure(command.RequestId, "blueprint_invalid_json", "Capture structured evidence contains invalid JSON.");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Failure(command.RequestId, "blueprint_crypto_failed", "Capture cryptographic verification failed.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(command.RequestId, "blueprint_access_denied", "Blueprint generation could not access the required local evidence safely.");
        }
        catch (IOException)
        {
            return Failure(command.RequestId, "blueprint_io_failed", "Blueprint generation failed while reading or publishing local evidence.");
        }
        catch (OverflowException)
        {
            return Failure(command.RequestId, "blueprint_limits_exceeded", "Capture size accounting exceeded the configured Blueprint limits.");
        }
    }

    public string ResolveSanitizedCaptureRoot(string targetId, string captureId)
    {
        var safeTarget = AevrixDataPaths.SafeTargetId(targetId);
        if (!EngineProtocol.IsSafeCaptureId(captureId))
        {
            throw new ArgumentException("Capture id is invalid.", nameof(captureId));
        }

        var root = Path.GetFullPath(_researchRuntimeRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, "research-artifacts", safeTarget, captureId, "sanitized"));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("Capture path escaped the AEVRIX ResearchRuntime root.");
        }
        return candidate;
    }

    private static string DefaultResearchRuntimeRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "AEVRIX", "ResearchRuntime");
    }

    private static EngineResponse Failure(string requestId, string code, string message) =>
        new(requestId, false, code, message);
}
