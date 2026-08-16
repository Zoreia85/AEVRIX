using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record BlueprintSynthesisLimits(
    int MaxArtifacts = 10_000,
    long MaxArtifactBytes = 8 * 1024 * 1024,
    long MaxTotalArtifactBytes = 128 * 1024 * 1024,
    long MaxManifestBytes = 2 * 1024 * 1024)
{
    public BlueprintSynthesisLimits Validate()
    {
        if (MaxArtifacts is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxArtifacts));
        }
        if (MaxArtifactBytes is < 1 or > 256L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxArtifactBytes));
        }
        if (MaxTotalArtifactBytes < MaxArtifactBytes || MaxTotalArtifactBytes > 4L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTotalArtifactBytes));
        }
        if (MaxManifestBytes is < 1 or > 32L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxManifestBytes));
        }
        return this;
    }
}

public sealed record BlueprintSynthesisResult(
    string CaptureId,
    int ImportedArtifacts,
    int VerifiedArtifacts,
    ProjectBlueprint Blueprint,
    BlueprintExportResult Export);

public sealed class ProjectBlueprintSynthesisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly AevrixDataPaths _paths;
    private readonly ProjectRepository _projects;
    private readonly EvidenceStore _evidence;
    private readonly ProjectBlueprintExporter _exporter;
    private readonly BlueprintSynthesisLimits _limits;

    public ProjectBlueprintSynthesisService(
        AevrixDataPaths paths,
        ProjectRepository? projects = null,
        EvidenceStore? evidence = null,
        ProjectBlueprintExporter? exporter = null,
        BlueprintSynthesisLimits? limits = null)
    {
        _paths = paths.EnsureCreated();
        _projects = projects ?? new ProjectRepository(_paths);
        _evidence = evidence ?? new EvidenceStore(_paths);
        _exporter = exporter ?? new ProjectBlueprintExporter();
        _limits = (limits ?? new BlueprintSynthesisLimits()).Validate();
    }

    public async Task<BlueprintSynthesisResult> SynthesizeCaptureAsync(
        Guid projectId,
        string captureId,
        string sanitizedCaptureRoot,
        bool includeRawEvidenceReferences = false,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }
        if (!EngineProtocol.IsSafeCaptureId(captureId))
        {
            throw new ArgumentException("Capture id is invalid.", nameof(captureId));
        }
        if (includeRawEvidenceReferences)
        {
            throw new InvalidOperationException(
                "Raw/quarantine evidence references are not exported by the default Blueprint policy.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedCaptureRoot);

        var envelope = await _projects.LoadAsync(projectId, cancellationToken);
        if (envelope.Project.Domain is not ProjectDomain.Web)
        {
            throw new NotSupportedException("Evidence-to-Blueprint synthesis is currently promoted only for web captures.");
        }

        var root = Path.GetFullPath(sanitizedCaptureRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Sanitized capture root was not found: {root}");
        }
        RejectReparsePoint(root, root);

        var manifestPath = Path.Combine(root, "capture-manifest.json");
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists)
        {
            throw new FileNotFoundException("Research capture manifest was not found.", manifestPath);
        }
        if (manifestInfo.Length > _limits.MaxManifestBytes)
        {
            throw new InvalidDataException("Research capture manifest exceeds the configured size limit.");
        }
        RejectReparsePoint(root, manifestPath);

        var manifest = await LoadCaptureManifestAsync(manifestPath, cancellationToken);
        ValidateManifest(manifest, envelope.Project, captureId);

        if (manifest.Artifacts.Count > _limits.MaxArtifacts)
        {
            throw new InvalidDataException("Research capture manifest artifact count exceeds the configured limit.");
        }
        var importCandidates = manifest.Artifacts
            .Where(item => !string.Equals(item.Classification, "quarantine", StringComparison.Ordinal))
            .ToArray();
        if (importCandidates.Length > _limits.MaxArtifacts)
        {
            throw new InvalidDataException("Research capture artifact count exceeds the configured limit.");
        }

        long totalBytes = 0;
        var imported = new List<StoredEvidenceArtifact>(importCandidates.Length + 1);
        var validatedArtifactPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in importCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureRelativePath = ValidateManifestArtifact(artifact);
            if (!validatedArtifactPaths.Add(captureRelativePath))
            {
                throw new InvalidDataException($"Research capture manifest contains duplicate normalized artifact path: {captureRelativePath}");
            }

            if (artifact.SizeBytes > _limits.MaxArtifactBytes)
            {
                throw new InvalidDataException($"Capture artifact exceeds the per-file limit: {artifact.RelativePath}");
            }
            totalBytes = checked(totalBytes + artifact.SizeBytes);
            if (totalBytes > _limits.MaxTotalArtifactBytes)
            {
                throw new InvalidDataException("Research capture exceeds the configured aggregate artifact size limit.");
            }

            var sourcePath = ResolveContainedArtifactPath(root, captureRelativePath);
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Manifest artifact was not found.", sourcePath);
            }
            if (info.Length != artifact.SizeBytes)
            {
                throw new InvalidDataException($"Manifest size mismatch for {artifact.RelativePath}.");
            }

            var actualHash = await ComputeSha256Async(sourcePath, cancellationToken);
            if (!string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Manifest SHA-256 mismatch for {artifact.RelativePath}.");
            }

            var sourceUri = await TryReadSanitizedSourceUriAsync(sourcePath, captureRelativePath, cancellationToken);
            var stored = await _evidence.StoreCaptureFileAsync(
                projectId,
                captureId,
                sourcePath,
                MapClassification(artifact.Classification),
                KindFor(captureRelativePath),
                artifact.MediaType,
                EvidenceBasis.Observed,
                sourceUri,
                $"Research capture artifact {captureRelativePath}",
                captureRelativePath,
                cancellationToken);
            imported.Add(stored);
        }

        // The manifest is the provenance root for the capture. It is stored only after every listed artifact validates.
        var storedManifest = await _evidence.StoreCaptureFileAsync(
            projectId,
            captureId,
            manifestPath,
            EvidenceClassification.Sanitized,
            "capture-manifest",
            "application/json",
            EvidenceBasis.Observed,
            sourceUri: null,
            description: "Validated Research Lab capture manifest",
            captureRelativePath: "capture-manifest.json",
            cancellationToken: cancellationToken);
        imported.Add(storedManifest);

        var captureEvidence = (await _evidence.ReadIndexAsync(projectId, cancellationToken))
            .Where(item => string.Equals(item.CaptureId, captureId, StringComparison.Ordinal))
            .Where(item => item.Classification is not EvidenceClassification.Quarantine)
            .ToArray();

        var verifiedCount = 0;
        foreach (var item in captureEvidence)
        {
            if (!await _evidence.VerifyAsync(item, cancellationToken))
            {
                throw new InvalidDataException($"Evidence Store integrity verification failed for {item.EvidenceId}.");
            }
            verifiedCount++;
        }

        var evidenceReferences = AggregateEvidenceReferences(captureEvidence);
        var evidenceByCapturePath = captureEvidence
            .Where(item => !string.IsNullOrWhiteSpace(item.CaptureRelativePath))
            .GroupBy(item => item.CaptureRelativePath!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        var coverage = await ReadCoverageAsync(root, validatedArtifactPaths, cancellationToken);
        var states = await ReadStatesAsync(root, validatedArtifactPaths, evidenceByCapturePath, cancellationToken);
        var endpoints = await ReadEndpointsAsync(root, validatedArtifactPaths, evidenceByCapturePath, states, cancellationToken);

        var architecture = BuildArchitecture(states, endpoints);
        var relationships = BuildArchitectureRelationships(states, endpoints, architecture);
        var uiComponents = BuildUiComponents(states);

        // The current sanitized capture does not yet export transition edges from the checkpoint DB.
        // Do not invent ordered workflows from an unordered set of states.
        IReadOnlyList<WorkflowModel> workflows = [];
        IReadOnlyList<BehavioralModel> behavioralModels = [];

        var readiness = BuildReadiness(coverage, verifiedCount, captureEvidence.Length);
        var limitations = BuildLimitations(coverage, workflows, behavioralModels);
        var openQuestions = BuildOpenQuestions(coverage, workflows, behavioralModels);

        var blueprint = new ProjectBlueprint(
            ProjectBlueprint.CurrentSchemaVersion,
            envelope.Project.Id,
            envelope.Project.Name,
            envelope.Project.TargetId,
            envelope.Project.Domain,
            DateTimeOffset.UtcNow,
            evidenceReferences,
            architecture,
            relationships,
            workflows,
            endpoints,
            uiComponents,
            behavioralModels,
            readiness,
            limitations,
            openQuestions).Validate();

        var exportRoot = Path.Combine(_paths.ProjectBlueprintRoot(projectId), captureId);
        var export = await _exporter.ExportAsync(blueprint, exportRoot, cancellationToken);
        return new BlueprintSynthesisResult(captureId, imported.Count, verifiedCount, blueprint, export);
    }

    private static async Task<CaptureManifestDocument> LoadCaptureManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<CaptureManifestDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Research capture manifest is empty or invalid.");
    }

    private static void ValidateManifest(CaptureManifestDocument manifest, CaptureProject project, string captureId)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported Research capture manifest schema {manifest.SchemaVersion}.");
        }
        if (!string.Equals(manifest.CaptureId, captureId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Research capture manifest id does not match the requested capture.");
        }
        if (!string.Equals(manifest.TargetId, project.TargetId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Research capture target does not match the project target.");
        }
        if (manifest.RawArtifactsInGit)
        {
            throw new InvalidDataException("Capture manifest incorrectly declares raw artifacts as Git-safe.");
        }
        if (manifest.Artifacts is null)
        {
            throw new InvalidDataException("Research capture manifest has no artifact list.");
        }

        var normalizedPaths = manifest.Artifacts.Select(ValidateManifestArtifact).ToArray();
        var duplicate = normalizedPaths
            .GroupBy(item => item, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Research capture manifest contains duplicate normalized artifact path: {duplicate.Key}");
        }
    }

    private static string ValidateManifestArtifact(CaptureArtifactDocument artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.RelativePath)
            || Path.IsPathRooted(artifact.RelativePath)
            || artifact.RelativePath.StartsWith("/", StringComparison.Ordinal)
            || artifact.RelativePath.Contains('\\')
            || artifact.RelativePath.Contains(':'))
        {
            throw new InvalidDataException("Capture artifact path must use a contained POSIX-style relative path.");
        }
        var normalizedPath = artifact.RelativePath;
        var pathParts = normalizedPath.Split('/');
        if (pathParts.Any(part => string.IsNullOrEmpty(part) || part is "." or ".."))
        {
            throw new InvalidDataException("Capture artifact path contains an empty or traversal segment.");
        }
        if (artifact.Sha256.Length != 64 || artifact.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException($"Capture artifact SHA-256 is invalid: {artifact.RelativePath}");
        }
        if (artifact.SizeBytes < 0)
        {
            throw new InvalidDataException($"Capture artifact size is invalid: {artifact.RelativePath}");
        }
        if (string.IsNullOrWhiteSpace(artifact.MediaType))
        {
            throw new InvalidDataException($"Capture artifact media type is missing: {artifact.RelativePath}");
        }
        if (artifact.Classification is not ("sanitized" or "neutral-knowledge" or "quarantine"))
        {
            throw new InvalidDataException($"Capture artifact classification is unsupported: {artifact.RelativePath}");
        }
        return normalizedPath;
    }

    private static string ResolveContainedArtifactPath(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Capture artifact escaped its root: {relativePath}");
        }
        RejectReparsePoint(root, full);
        return full;
    }

    private static void RejectReparsePoint(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateFull = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(rootFull, candidateFull);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capture path escaped its declared root.");
        }

        CheckAttributes(rootFull);
        var current = rootFull;
        if (!string.Equals(relative, ".", StringComparison.Ordinal))
        {
            foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (File.Exists(current) || Directory.Exists(current))
                {
                    CheckAttributes(current);
                }
            }
        }

        static void CheckAttributes(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Capture path contains a reparse point and was rejected: {path}");
            }
        }
    }

    private static async Task<Uri?> TryReadSanitizedSourceUriAsync(
        string sourcePath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (!relativePath.EndsWith("/state.json", StringComparison.Ordinal)
            && !string.Equals(relativePath, "state.json", StringComparison.Ordinal))
        {
            return null;
        }

        JsonDocument? document = null;
        try
        {
            await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("state", out var state)
                || !state.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind is not JsonValueKind.String
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri))
            {
                return null;
            }
            return SanitizeUri(uri);
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static async Task<CoverageDocument> ReadCoverageAsync(
        string root,
        IReadOnlySet<string> validatedArtifactPaths,
        CancellationToken cancellationToken)
    {
        const string capturePath = "coverage.json";
        var path = Path.Combine(root, capturePath);
        if (!File.Exists(path))
        {
            return new CoverageDocument();
        }
        RequireValidatedStructuredArtifact(capturePath, validatedArtifactPaths);
        RejectReparsePoint(root, path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<CoverageDocument>(stream, JsonOptions, cancellationToken) ?? new CoverageDocument();
    }

    private static async Task<IReadOnlyList<ApiEndpointModel>> ReadEndpointsAsync(
        string root,
        IReadOnlySet<string> validatedArtifactPaths,
        IReadOnlyDictionary<string, string[]> evidenceByCapturePath,
        IReadOnlyList<StateDocument> states,
        CancellationToken cancellationToken)
    {
        const string capturePath = "endpoints.json";
        var path = Path.Combine(root, capturePath);
        if (!File.Exists(path))
        {
            return [];
        }
        RequireValidatedStructuredArtifact(capturePath, validatedArtifactPaths);
        RejectReparsePoint(root, path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var documents = await JsonSerializer.DeserializeAsync<List<EndpointDocument>>(stream, JsonOptions, cancellationToken) ?? [];
        var endpointFileEvidence = evidenceByCapturePath.TryGetValue("endpoints.json", out var ids) ? ids : [];

        return documents
            .Where(item => !string.IsNullOrWhiteSpace(item.Method) && !string.IsNullOrWhiteSpace(item.Url))
            .Select(item => new
            {
                Item = item,
                Path = SanitizeEndpoint(item.Url),
                EndpointKey = ComputeNetworkEndpointKey(item.Method, item.Url)
            })
            .Where(item => item.Path is not null && item.EndpointKey is not null)
            .GroupBy(item => $"{item.Item.Method.Trim().ToUpperInvariant()} {item.Path}", StringComparer.Ordinal)
            .Select(group =>
            {
                var method = group.First().Item.Method.Trim().ToUpperInvariant();
                var endpointPath = group.First().Path!;
                var endpointKeys = group.Select(item => item.EndpointKey!).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
                var stateEvidence = states
                    .Where(state => (state.State?.NetworkSchemaKeys ?? []).Any(endpointKeys.Contains))
                    .SelectMany(state => state.EvidenceIds);
                var evidenceIds = endpointFileEvidence
                    .Concat(stateEvidence)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                var schemas = group
                    .SelectMany(item => FlattenSafeSchema(item.Item.Schema))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                var pagination = group
                    .SelectMany(item => item.Item.PaginationHints ?? [])
                    .Where(NotSensitiveValue)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                if (evidenceIds.Length == 0)
                {
                    return null;
                }
                return new ApiEndpointModel(
                    Id: StableId("api", method + " " + endpointPath),
                    Method: method,
                    PathTemplate: endpointPath,
                    RequestSchemaKeys: [],
                    ResponseSchemaKeys: schemas,
                    PaginationHints: pagination,
                    Basis: EvidenceBasis.Observed,
                    Confidence: ConfidenceScore.FromPercent(100),
                    EvidenceIds: evidenceIds);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.Method, StringComparer.Ordinal)
            .ThenBy(item => item.PathTemplate, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<IReadOnlyList<StateDocument>> ReadStatesAsync(
        string root,
        IReadOnlySet<string> validatedArtifactPaths,
        IReadOnlyDictionary<string, string[]> evidenceByCapturePath,
        CancellationToken cancellationToken)
    {
        var manifestStatePaths = validatedArtifactPaths
            .Where(IsCanonicalStateDocumentPath)
            .ToHashSet(StringComparer.Ordinal);
        var statesRoot = Path.Combine(root, "states");
        if (!Directory.Exists(statesRoot))
        {
            return [];
        }
        RejectReparsePoint(root, statesRoot);

        // Detect semantic state files that exist on disk but were not covered by the verified manifest.
        foreach (var stateDirectory in Directory.EnumerateDirectories(statesRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(root, stateDirectory);
            var physicalStatePath = Path.Combine(stateDirectory, "state.json");
            if (!File.Exists(physicalStatePath))
            {
                continue;
            }
            RejectReparsePoint(root, physicalStatePath);
            var physicalCapturePath = Path.GetRelativePath(root, physicalStatePath).Replace('\\', '/');
            if (!manifestStatePaths.Contains(physicalCapturePath))
            {
                throw new InvalidDataException($"Structured capture state is not covered by the validated manifest: {physicalCapturePath}");
            }
        }

        var result = new List<StateDocument>();
        foreach (var capturePath in manifestStatePaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statePath = ResolveContainedArtifactPath(root, capturePath);
            await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<StateDocument>(stream, JsonOptions, cancellationToken);
            if (state is null)
            {
                continue;
            }
            state = state with
            {
                EvidenceIds = evidenceByCapturePath.TryGetValue(capturePath, out var ids) ? ids : [],
                StateId = capturePath.Split('/')[1]
            };
            result.Add(state);
        }
        return result;
    }

    private static bool IsCanonicalStateDocumentPath(string capturePath)
    {
        var parts = capturePath.Split('/');
        return parts.Length == 3
            && string.Equals(parts[0], "states", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(parts[1])
            && string.Equals(parts[2], "state.json", StringComparison.Ordinal);
    }

    private static void RequireValidatedStructuredArtifact(string capturePath, IReadOnlySet<string> validatedArtifactPaths)
    {
        if (!validatedArtifactPaths.Contains(capturePath))
        {
            throw new InvalidDataException($"Structured capture file is not covered by the validated manifest: {capturePath}");
        }
    }

    private static IReadOnlyList<ArchitectureElement> BuildArchitecture(
        IReadOnlyList<StateDocument> states,
        IReadOnlyList<ApiEndpointModel> endpoints)
    {
        var elements = new List<ArchitectureElement>();
        var stateEvidence = states.SelectMany(item => item.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
        if (states.Count > 0 && stateEvidence.Length > 0)
        {
            elements.Add(new ArchitectureElement(
                "browser",
                "Research Browser",
                ArchitectureElementKind.Browser,
                EvidenceBasis.Observed,
                ConfidenceScore.FromPercent(100),
                stateEvidence));
            elements.Add(new ArchitectureElement(
                "frontend",
                "Observed web application surface",
                ArchitectureElementKind.Frontend,
                EvidenceBasis.Observed,
                ConfidenceScore.FromPercent(100),
                stateEvidence,
                new Dictionary<string, string>
                {
                    ["observedStates"] = states.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
        }
        var endpointEvidence = endpoints.SelectMany(item => item.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
        if (endpoints.Count > 0 && endpointEvidence.Length > 0)
        {
            elements.Add(new ArchitectureElement(
                "api-surface",
                "Observed first-party API surface",
                ArchitectureElementKind.ApiService,
                EvidenceBasis.Observed,
                ConfidenceScore.FromPercent(100),
                endpointEvidence,
                new Dictionary<string, string>
                {
                    ["observedEndpoints"] = endpoints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
        }
        return elements;
    }

    private static IReadOnlyList<ArchitectureRelationship> BuildArchitectureRelationships(
        IReadOnlyList<StateDocument> states,
        IReadOnlyList<ApiEndpointModel> endpoints,
        IReadOnlyList<ArchitectureElement> elements)
    {
        var ids = elements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var relationships = new List<ArchitectureRelationship>();
        if (ids.Contains("browser") && ids.Contains("frontend"))
        {
            relationships.Add(new ArchitectureRelationship(
                "browser",
                "frontend",
                "renders observed application states",
                EvidenceBasis.Observed,
                ConfidenceScore.FromPercent(100),
                states.SelectMany(item => item.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray()));
        }
        if (ids.Contains("frontend") && ids.Contains("api-surface"))
        {
            relationships.Add(new ArchitectureRelationship(
                "frontend",
                "api-surface",
                "observed first-party XHR/fetch traffic",
                EvidenceBasis.Observed,
                ConfidenceScore.FromPercent(100),
                endpoints.SelectMany(item => item.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray()));
        }
        return relationships;
    }

    private static IReadOnlyList<UiComponentModel> BuildUiComponents(IReadOnlyList<StateDocument> states)
    {
        var controls = states.SelectMany(state => (state.Controls ?? []).Select(control => new { State = state, Control = control }));
        return controls
            .Where(item => item.State.EvidenceIds.Count > 0)
            .Where(item => !string.IsNullOrWhiteSpace(item.Control.Label) || !string.IsNullOrWhiteSpace(item.Control.Role))
            .GroupBy(item => UiKey(item.Control), StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = group.First().Control;
                var stateIds = group.Select(item => item.State.StateId).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
                var evidenceIds = group.SelectMany(item => item.State.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
                var outputs = group.Select(item => SanitizeHref(item.Control.Href)).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
                var inputs = group.Select(item => item.Control.ElementType).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
                return new UiComponentModel(
                    Id: StableId("ui", group.Key),
                    Name: FirstNonEmpty(representative.Label, representative.Role, representative.ElementType, "control"),
                    ComponentType: FirstNonEmpty(representative.SemanticKind, representative.Role, representative.ElementType, "control"),
                    States: stateIds,
                    Inputs: inputs,
                    Outputs: outputs,
                    Basis: EvidenceBasis.Observed,
                    Confidence: ConfidenceScore.FromPercent(100),
                    EvidenceIds: evidenceIds);
            })
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<EvidenceReference> AggregateEvidenceReferences(IReadOnlyList<StoredEvidenceArtifact> artifacts)
    {
        return artifacts
            .GroupBy(item => item.EvidenceId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.OrderBy(item => item.StoredAt).First();
                var descriptions = group.Select(item => item.Description).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray();
                return new EvidenceReference(
                    first.EvidenceId,
                    string.Join("+", group.Select(item => item.Kind).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal)),
                    first.RelativePath,
                    first.Sha256,
                    first.StoredAt,
                    group.Any(item => item.Basis is EvidenceBasis.ExperimentallyValidated) ? EvidenceBasis.ExperimentallyValidated : first.Basis,
                    descriptions.Length == 0 ? null : string.Join(" | ", descriptions));
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static ReproductionReadiness BuildReadiness(CoverageDocument coverage, int verifiedArtifacts, int artifactCount)
    {
        var structural = PercentOrZero(coverage.StructuralPercent);
        var dataApi = RatioPercent(coverage.Endpoints?.Inspected ?? 0, coverage.Endpoints?.Discovered ?? 0);
        var ui = RatioPercent(coverage.States?.Visited ?? 0, coverage.States?.Discovered ?? 0);
        var evidenceConfidence = artifactCount > 0 ? RatioPercent(verifiedArtifacts, artifactCount) : 0;

        return ReproductionReadiness.Calculate(
            structuralCoverage: structural,
            workflowCoverage: 0,
            dataApiCoverage: dataApi,
            uiCoverage: ui,
            behavioralCoverage: 0,
            evidenceConfidence: evidenceConfidence,
            unresolvedCriticalQuestions: 0,
            hasUnresolvedSessionInterruptions: (coverage.UnresolvedSessionInterruptions ?? 0) > 0,
            hasOpenPagination: (coverage.PaginationOpen ?? 0) > 0,
            hasMaterialEvidenceIntegrityFailure: false);
    }

    private static IReadOnlyList<string> BuildLimitations(
        CoverageDocument coverage,
        IReadOnlyList<WorkflowModel> workflows,
        IReadOnlyList<BehavioralModel> behavioralModels)
    {
        var result = new List<string>();
        if (workflows.Count == 0)
        {
            result.Add("Sanitized capture does not yet export durable state-transition edges; ordered workflows were not inferred from unordered states.");
        }
        if (behavioralModels.Count == 0)
        {
            result.Add("No controlled behavioral experiment/holdout evidence is present; Behavioral Similarity remains 0.");
        }
        if (!string.Equals(coverage.Status, "complete", StringComparison.OrdinalIgnoreCase))
        {
            result.Add("Research capture coverage is partial or unknown; readiness is intentionally conservative.");
        }
        return result;
    }

    private static IReadOnlyList<string> BuildOpenQuestions(
        CoverageDocument coverage,
        IReadOnlyList<WorkflowModel> workflows,
        IReadOnlyList<BehavioralModel> behavioralModels)
    {
        var result = new List<string>();
        if (workflows.Count == 0)
        {
            result.Add("Which transition edges and user journeys should be exported from the durable checkpoint graph in the next capture schema?");
        }
        if (behavioralModels.Count == 0)
        {
            result.Add("Which behaviors merit controlled synthetic experiments and independent holdout validation?");
        }
        if ((coverage.PaginationOpen ?? 0) > 0)
        {
            result.Add("Which paginated surfaces remain open and require additional authorized capture?");
        }
        return result;
    }

    private static EvidenceClassification MapClassification(string classification) => classification switch
    {
        "sanitized" => EvidenceClassification.Sanitized,
        "neutral-knowledge" => EvidenceClassification.NeutralKnowledge,
        "quarantine" => EvidenceClassification.Quarantine,
        _ => throw new InvalidDataException($"Unsupported evidence classification {classification}.")
    };

    private static string KindFor(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (string.Equals(normalized, "coverage.json", StringComparison.Ordinal)) return "capture-coverage";
        if (string.Equals(normalized, "endpoints.json", StringComparison.Ordinal)) return "network-endpoints";
        if (normalized.EndsWith("/state.json", StringComparison.Ordinal)) return "browser-state";
        if (normalized.EndsWith("/content.md", StringComparison.Ordinal)) return "rendered-content";
        return "capture-artifact";
    }

    private static Uri SanitizeUri(Uri uri)
    {
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    private static string? ComputeNetworkEndpointKey(string method, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }

        var authority = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
        var canonical = $"{method.Trim().ToUpperInvariant()} {uri.Scheme.ToLowerInvariant()}://{authority.ToLowerInvariant()}{uri.AbsolutePath}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static IEnumerable<string> FlattenSafeSchema(IReadOnlyList<JsonElement>? schema)
    {
        if (schema is null)
        {
            yield break;
        }

        foreach (var node in schema)
        {
            if (node.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            var path = node.TryGetProperty("path", out var pathElement) && pathElement.ValueKind is JsonValueKind.String
                ? pathElement.GetString() ?? "$"
                : "$";
            if (ContainsSensitiveSchemaPath(path))
            {
                continue;
            }
            var type = node.TryGetProperty("type", out var typeElement) && typeElement.ValueKind is JsonValueKind.String
                ? typeElement.GetString() ?? "unknown"
                : "unknown";

            if (node.TryGetProperty("keys", out var keysElement) && keysElement.ValueKind is JsonValueKind.Array)
            {
                var keys = keysElement.EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item) && NotSensitiveKey(item!))
                    .Select(item => item!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                if (keys.Length > 0)
                {
                    yield return $"{path}:{type}{{{string.Join(",", keys)}}}";
                    continue;
                }
            }

            yield return $"{path}:{type}";
        }
    }

    private static bool ContainsSensitiveSchemaPath(string path)
    {
        var lower = path.ToLowerInvariant();
        return new[]
        {
            "authorization", "cookie", "set-cookie", "token", "access_token", "refresh_token",
            "password", "passwd", "secret", "api_key", "apikey", "sessionid", "id_token"
        }.Any(marker => lower.Contains(marker, StringComparison.Ordinal));
    }

    private static string? SanitizeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            return null;
        }
        var sanitized = SanitizeUri(uri);
        return sanitized.GetLeftPart(UriPartial.Path);
    }

    private static string SanitizeHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http")
        {
            return SanitizeUri(absolute).GetLeftPart(UriPartial.Path);
        }
        var fragmentIndex = value.IndexOf('#');
        var queryIndex = value.IndexOf('?');
        var cut = new[] { fragmentIndex, queryIndex }.Where(index => index >= 0).DefaultIfEmpty(value.Length).Min();
        return value[..cut];
    }

    private static string UiKey(ControlDocument control)
    {
        return string.Join("|", new[]
        {
            FirstNonEmpty(control.SemanticKind, control.Role, control.ElementType, "control").Trim().ToLowerInvariant(),
            (control.Label ?? string.Empty).Trim().ToLowerInvariant(),
            SanitizeHref(control.Href).Trim().ToLowerInvariant()
        });
    }

    private static string StableId(string prefix, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}-{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string FirstNonEmpty(params string?[] values) => values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static bool NotSensitiveKey(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        return lower is not ("authorization" or "cookie" or "set-cookie" or "token" or "access_token" or "refresh_token" or "id_token" or "password" or "passwd" or "secret" or "api_key" or "apikey" or "sessionid");
    }

    private static bool NotSensitiveValue(string value)
    {
        var lower = value.ToLowerInvariant();
        return !lower.Contains("token=", StringComparison.Ordinal)
            && !lower.Contains("password=", StringComparison.Ordinal)
            && !lower.Contains("secret=", StringComparison.Ordinal)
            && !lower.Contains("authorization", StringComparison.Ordinal);
    }

    private static double RatioPercent(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return 0;
        }
        return Math.Round(Math.Clamp((double)numerator / denominator * 100d, 0d, 100d), 2);
    }

    private static double PercentOrZero(double? value) => value is >= 0 and <= 100 ? value.Value : 0;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record CaptureManifestDocument(
        int SchemaVersion,
        string CaptureId,
        string TargetId,
        string CreatedAt,
        bool RawArtifactsInGit,
        IReadOnlyList<CaptureArtifactDocument> Artifacts);

    private sealed record CaptureArtifactDocument(
        [property: System.Text.Json.Serialization.JsonPropertyName("relative_path")] string RelativePath,
        [property: System.Text.Json.Serialization.JsonPropertyName("sha256")] string Sha256,
        [property: System.Text.Json.Serialization.JsonPropertyName("size_bytes")] long SizeBytes,
        [property: System.Text.Json.Serialization.JsonPropertyName("media_type")] string MediaType,
        [property: System.Text.Json.Serialization.JsonPropertyName("classification")] string Classification);

    private sealed record CoverageDocument
    {
        public string? Status { get; init; }
        public double? StructuralPercent { get; init; }
        public CoverageCountDocument? States { get; init; }
        public CoverageCountDocument? Routes { get; init; }
        public CoverageCountDocument? Endpoints { get; init; }
        public int? PaginationOpen { get; init; }
        public int? UnresolvedSessionInterruptions { get; init; }
        public int? InaccessibleAreas { get; init; }
    }

    private sealed record CoverageCountDocument
    {
        public int Discovered { get; init; }
        public int Visited { get; init; }
        public int Inspected { get; init; }
        public int Queued { get; init; }
        public int Visiting { get; init; }
        public int Blocked { get; init; }
        public int Errors { get; init; }
    }

    private sealed record EndpointDocument
    {
        public string Method { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public int Status { get; init; }
        public string ContentType { get; init; } = string.Empty;
        public IReadOnlyList<string>? PaginationHints { get; init; }
        public IReadOnlyList<JsonElement>? Schema { get; init; }
    }

    private sealed record StateDocument
    {
        public StatePayloadDocument? State { get; init; }
        public IReadOnlyList<ControlDocument>? Controls { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string StateId { get; init; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<string> EvidenceIds { get; init; } = [];
    }

    private sealed record StatePayloadDocument
    {
        public string? Url { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("active_menu")]
        public IReadOnlyList<string>? ActiveMenu { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("active_tabs")]
        public IReadOnlyList<string>? ActiveTabs { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("open_modals")]
        public IReadOnlyList<string>? OpenModals { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("network_schema_keys")]
        public IReadOnlyList<string>? NetworkSchemaKeys { get; init; }
    }

    private sealed record ControlDocument
    {
        public string? Label { get; init; }
        public string? Role { get; init; }
        public string? Href { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("element_type")]
        public string? ElementType { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("semantic_kind")]
        public string? SemanticKind { get; init; }
        public bool Allowed { get; init; }
        public string? Reason { get; init; }
    }
}
