using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class StorageAndBlueprintTests
{
    [TestMethod]
    public async Task ProjectRepositoryPersistsAndReloadsCanonicalProject()
    {
        using var temp = new TemporaryDirectory();
        var paths = Paths(temp.Path);
        var repository = new ProjectRepository(paths);
        var project = CaptureProject.CreateWeb(
            "Study Example",
            "example-target",
            new Uri("https://example.com/app"));

        await repository.CreateAsync(project, metadata: new Dictionary<string, string> { ["purpose"] = "test" });
        var loaded = await repository.LoadAsync(project.Id);

        Assert.AreEqual(project.Id, loaded.Project.Id);
        Assert.AreEqual(ProjectDomain.Web, loaded.Project.Domain);
        Assert.AreEqual("test", loaded.Metadata["purpose"]);
    }

    [TestMethod]
    public async Task EvidenceStoreDeduplicatesByContentAndVerifiesIntegrity()
    {
        using var temp = new TemporaryDirectory();
        var paths = Paths(temp.Path);
        var project = CaptureProject.CreateWeb("Study", "target", new Uri("https://example.com"));
        await new ProjectRepository(paths).CreateAsync(project);

        var source = Path.Combine(temp.Path, "source.txt");
        await File.WriteAllTextAsync(source, "same evidence content");
        var store = new EvidenceStore(paths);

        var first = await store.StoreFileAsync(
            project.Id,
            "capture-001",
            source,
            EvidenceClassification.Sanitized,
            "text",
            "text/plain",
            EvidenceBasis.Observed);
        var second = await store.StoreFileAsync(
            project.Id,
            "capture-002",
            source,
            EvidenceClassification.Sanitized,
            "text",
            "text/plain",
            EvidenceBasis.Observed);

        Assert.AreEqual(first.Sha256, second.Sha256);
        Assert.AreEqual(first.RelativePath, second.RelativePath);
        Assert.IsTrue(await store.VerifyAsync(first));
        Assert.AreEqual(2, (await store.ReadIndexAsync(project.Id)).Count);
    }

    [TestMethod]
    public async Task BlueprintExporterCreatesGraphAndManifestWithHash()
    {
        using var temp = new TemporaryDirectory();
        var evidence = new EvidenceReference(
            "EV-1",
            "dom",
            "06_OFFLINE_EVIDENCE/page.html",
            new string('a', 64),
            DateTimeOffset.UtcNow,
            EvidenceBasis.Observed,
            "Observed page state");
        var readiness = ReproductionReadiness.Calculate(95, 95, 90, 90, 92, 95, 0, false, false, false);
        var blueprint = new ProjectBlueprint(
            ProjectBlueprint.CurrentSchemaVersion,
            Guid.NewGuid(),
            "Blueprint test",
            "example-target",
            ProjectDomain.Web,
            DateTimeOffset.UtcNow,
            [evidence],
            [new ArchitectureElement(
                "frontend",
                "Frontend",
                ArchitectureElementKind.Frontend,
                EvidenceBasis.Observed,
                ConfidenceScore.FromPercent(99),
                ["EV-1"])],
            [],
            [new WorkflowModel(
                "wf-login",
                "Login",
                [new WorkflowStep("open", "Open application", "/app", EvidenceBasis.Observed, ConfidenceScore.FromPercent(99), ["EV-1"])],
                [],
                ["Authenticated area visible"],
                ConfidenceScore.FromPercent(95))],
            [],
            [],
            [],
            readiness,
            [],
            []);

        var result = await new ProjectBlueprintExporter().ExportAsync(blueprint, Path.Combine(temp.Path, "export"));

        Assert.AreEqual(64, result.BlueprintSha256.Length);
        Assert.IsTrue(File.Exists(Path.Combine(result.RootPath, "00_MANIFEST", "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(result.RootPath, "01_ARCHITECTURE", "architecture.mmd")));
        Assert.IsTrue(File.Exists(Path.Combine(result.RootPath, "01_ARCHITECTURE", "architecture.graphml")));
        Assert.IsTrue(File.Exists(Path.Combine(result.RootPath, "08_REPORT", "executive-summary.md")));
    }

    [TestMethod]
    public void BlueprintValidationRejectsMissingEvidenceReferences()
    {
        var blueprint = new ProjectBlueprint(
            ProjectBlueprint.CurrentSchemaVersion,
            Guid.NewGuid(),
            "Invalid",
            "target",
            ProjectDomain.Web,
            DateTimeOffset.UtcNow,
            [],
            [new ArchitectureElement(
                "frontend",
                "Frontend",
                ArchitectureElementKind.Frontend,
                EvidenceBasis.Inferred,
                ConfidenceScore.FromPercent(80),
                ["MISSING-EVIDENCE"])],
            [],
            [],
            [],
            [],
            [],
            ReproductionReadiness.Calculate(50, 50, 50, 50, 50, 50, 3, false, false, false),
            [],
            []);

        InvalidOperationException? observed = null;
        try
        {
            blueprint.Validate();
        }
        catch (InvalidOperationException exception)
        {
            observed = exception;
        }

        Assert.IsNotNull(observed, "Blueprint.Validate() must reject references to evidence ids that do not exist in the blueprint.");
        StringAssert.Contains(observed.Message, "missing evidence ids");
    }


    [TestMethod]
    public async Task SynthesisImportsCaptureIdempotentlyAndSanitizesAggregatedEndpointsAndUi()
    {
        using var temp = new TemporaryDirectory();
        var paths = Paths(temp.Path);
        var project = CaptureProject.CreateWeb("Synthesis fixture", "fixture-target", new Uri("https://example.com/app"));
        await new ProjectRepository(paths).CreateAsync(project);
        var captureId = "capture-aggregate-001";
        var captureRoot = Path.Combine(temp.Path, "research-runtime", captureId, "sanitized");
        Directory.CreateDirectory(captureRoot);

        var endpointKey = NetworkEndpointKey("GET", "https://example.com/api/state?token=not-exported");
        await WriteJsonAsync(Path.Combine(captureRoot, "coverage.json"), new
        {
            status = "complete",
            structuralPercent = 100d,
            states = new { discovered = 2, visited = 2, queued = 0, visiting = 0, blocked = 0, errors = 0 },
            routes = new { discovered = 2, visited = 2 },
            endpoints = new { discovered = 1, inspected = 1 },
            paginationOpen = 0,
            unresolvedSessionInterruptions = 0,
            inaccessibleAreas = 0
        });
        await WriteJsonAsync(Path.Combine(captureRoot, "endpoints.json"), new object[]
        {
            new
            {
                method = "GET",
                url = "https://example.com/api/state?token=super-secret&view=one#fragment",
                status = 200,
                contentType = "application/json",
                paginationHints = new[] { "next" },
                schema = new object[] { new { path = "$", type = "object", keys = new[] { "id", "token" } } }
            },
            new
            {
                method = "GET",
                url = "https://example.com/api/state?view=two",
                status = 200,
                contentType = "application/json",
                paginationHints = new[] { "cursor", "next" },
                schema = new object[] { new { path = "$", type = "object", keys = new[] { "name", "next" } } }
            }
        });

        await WriteStateAsync(captureRoot, "state-one", "https://example.com/app?access_token=never-export#one", endpointKey, "https://example.com/detail?token=one#fragment");
        await WriteStateAsync(captureRoot, "state-two", "https://example.com/app/results?session=never-export#two", endpointKey, "https://example.com/detail?token=two#fragment");
        await File.WriteAllTextAsync(Path.Combine(captureRoot, "states", "state-one", "content.md"), "State one sanitized");
        await File.WriteAllTextAsync(Path.Combine(captureRoot, "states", "state-two", "content.md"), "State two sanitized");
        await WriteCaptureManifestAsync(captureRoot, captureId, project.TargetId);

        var service = new ProjectBlueprintSynthesisService(paths);
        var first = await service.SynthesizeCaptureAsync(project.Id, captureId, captureRoot);
        var indexPath = Path.Combine(paths.ProjectEvidenceRoot(project.Id), "index.ndjson");
        var firstIndexLines = File.ReadAllLines(indexPath).Length;
        var second = await service.SynthesizeCaptureAsync(project.Id, captureId, captureRoot);
        var secondIndexLines = File.ReadAllLines(indexPath).Length;

        Assert.AreEqual(firstIndexLines, secondIndexLines, "Repeated synthesis must not append duplicate evidence index rows.");
        Assert.AreEqual(first.Blueprint.Evidence.Count, second.Blueprint.Evidence.Count);
        Assert.AreEqual(1, first.Blueprint.ApiEndpoints.Count);
        var endpoint = first.Blueprint.ApiEndpoints.Single();
        Assert.AreEqual("https://example.com/api/state", endpoint.PathTemplate);
        Assert.IsFalse(endpoint.PathTemplate.Contains('?'));
        Assert.IsFalse(endpoint.PathTemplate.Contains('#'));
        Assert.IsTrue(endpoint.EvidenceIds.Count >= 3, "Endpoint evidence should aggregate endpoints.json plus the observed states carrying its endpoint key.");
        Assert.IsTrue(endpoint.ResponseSchemaKeys.Any(item => item.Contains("id", StringComparison.Ordinal)));
        Assert.IsTrue(endpoint.ResponseSchemaKeys.Any(item => item.Contains("name", StringComparison.Ordinal)));
        Assert.IsFalse(endpoint.ResponseSchemaKeys.Any(item => item.Contains("token", StringComparison.OrdinalIgnoreCase)));
        CollectionAssert.AreEquivalent(new[] { "cursor", "next" }, endpoint.PaginationHints.ToArray());

        Assert.AreEqual(1, first.Blueprint.UiComponents.Count);
        var ui = first.Blueprint.UiComponents.Single();
        CollectionAssert.AreEquivalent(new[] { "state-one", "state-two" }, ui.States.ToArray());
        CollectionAssert.AreEquivalent(new[] { "https://example.com/detail" }, ui.Outputs.ToArray());
        Assert.IsTrue(ui.EvidenceIds.Count >= 2);

        Assert.AreEqual(0, first.Blueprint.Workflows.Count, "Unordered states must not be fabricated into workflows.");
        Assert.AreEqual(0, first.Blueprint.BehavioralModels.Count, "Behavioral similarity requires controlled experiments and holdouts.");
        Assert.AreEqual(0d, first.Blueprint.Readiness.Dimensions.Single(item => item.Name == "Workflow Coverage").Percent);
        Assert.AreEqual(0d, first.Blueprint.Readiness.Dimensions.Single(item => item.Name == "Behavioral Similarity").Percent);
        Assert.IsTrue(File.Exists(Path.Combine(first.Export.RootPath, "00_MANIFEST", "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(first.Export.RootPath, "08_REPORT", "executive-summary.md")));
    }

    [TestMethod]
    public async Task SynthesisFailsClosedOnTamperedArtifactAndPathTraversal()
    {
        using var temp = new TemporaryDirectory();
        var paths = Paths(temp.Path);
        var project = CaptureProject.CreateWeb("Integrity fixture", "fixture-target", new Uri("https://example.com/app"));
        await new ProjectRepository(paths).CreateAsync(project);
        var captureId = "capture-integrity-001";
        var root = Path.Combine(temp.Path, "capture", "sanitized");
        Directory.CreateDirectory(root);
        await WriteJsonAsync(Path.Combine(root, "coverage.json"), new { status = "partial", structuralPercent = 0d });
        await WriteCaptureManifestAsync(root, captureId, project.TargetId);
        await File.AppendAllTextAsync(Path.Combine(root, "coverage.json"), "tampered");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await new ProjectBlueprintSynthesisService(paths).SynthesizeCaptureAsync(project.Id, captureId, root));

        var traversalRoot = Path.Combine(temp.Path, "traversal", "sanitized");
        Directory.CreateDirectory(traversalRoot);
        var bytes = System.Text.Encoding.UTF8.GetBytes("x");
        var manifest = new
        {
            schemaVersion = 1,
            captureId,
            targetId = project.TargetId,
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            rawArtifactsInGit = false,
            artifacts = new[]
            {
                new
                {
                    relative_path = "../escape.json",
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                    size_bytes = bytes.LongLength,
                    media_type = "application/json",
                    classification = "sanitized"
                }
            }
        };
        await WriteJsonAsync(Path.Combine(traversalRoot, "capture-manifest.json"), manifest);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await new ProjectBlueprintSynthesisService(paths).SynthesizeCaptureAsync(project.Id, captureId, traversalRoot));
    }

    [TestMethod]
    public async Task SynthesisRejectsStructuredFilesThatAreNotCoveredByValidatedManifest()
    {
        using var temp = new TemporaryDirectory();
        var paths = Paths(temp.Path);
        var project = CaptureProject.CreateWeb("Manifest authority fixture", "fixture-target", new Uri("https://example.com/app"));
        await new ProjectRepository(paths).CreateAsync(project);
        var captureId = "capture-manifest-authority-001";

        var endpointRoot = Path.Combine(temp.Path, "unmanifested-endpoint", "sanitized");
        Directory.CreateDirectory(endpointRoot);
        await WriteJsonAsync(Path.Combine(endpointRoot, "coverage.json"), new { status = "partial", structuralPercent = 0d });
        await WriteCaptureManifestAsync(endpointRoot, captureId, project.TargetId);
        await WriteJsonAsync(Path.Combine(endpointRoot, "endpoints.json"), new object[]
        {
            new { method = "GET", url = "https://example.com/api/unmanifested", status = 200, contentType = "application/json", paginationHints = Array.Empty<string>(), schema = Array.Empty<object>() }
        });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await new ProjectBlueprintSynthesisService(paths).SynthesizeCaptureAsync(project.Id, captureId, endpointRoot));

        var stateRoot = Path.Combine(temp.Path, "unmanifested-state", "sanitized");
        Directory.CreateDirectory(stateRoot);
        await WriteJsonAsync(Path.Combine(stateRoot, "coverage.json"), new { status = "partial", structuralPercent = 0d });
        await WriteCaptureManifestAsync(stateRoot, captureId, project.TargetId);
        await WriteStateAsync(stateRoot, "rogue-state", "https://example.com/rogue", NetworkEndpointKey("GET", "https://example.com/api/rogue"), "https://example.com/rogue/detail");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await new ProjectBlueprintSynthesisService(paths).SynthesizeCaptureAsync(project.Id, captureId, stateRoot));
    }

    [TestMethod]
    public async Task GenerateBlueprintCommandHandlerRoutesVerifiedCaptureIntoSynthesis()
    {
        using var temp = new TemporaryDirectory();
        var paths = Paths(Path.Combine(temp.Path, "user"));
        var project = CaptureProject.CreateWeb("Command fixture", "fixture-target", new Uri("https://example.com/app"));
        await new ProjectRepository(paths).CreateAsync(project);
        var captureId = "capture-command-001";
        var researchRuntime = Path.Combine(temp.Path, "research-runtime");
        var root = Path.Combine(researchRuntime, "research-artifacts", project.TargetId, captureId, "sanitized");
        Directory.CreateDirectory(root);
        await WriteJsonAsync(Path.Combine(root, "coverage.json"), new
        {
            status = "partial",
            structuralPercent = 0d,
            states = new { discovered = 0, visited = 0 },
            routes = new { discovered = 0, visited = 0 },
            endpoints = new { discovered = 0, inspected = 0 },
            paginationOpen = 0,
            unresolvedSessionInterruptions = 0,
            inaccessibleAreas = 0
        });
        await WriteCaptureManifestAsync(root, captureId, project.TargetId);

        var handler = new BlueprintCommandHandler(paths, researchRuntime);
        var response = await handler.HandleAsync(new GenerateBlueprintCommand(
            "request-blueprint-001",
            project.Id.ToString("D"),
            captureId));

        Assert.IsTrue(response.Success);
        Assert.AreEqual("blueprint_generated", response.Code);
        Assert.IsInstanceOfType<BlueprintCommandResultData>(response.Data);
        var data = (BlueprintCommandResultData)response.Data!;
        Assert.AreEqual(captureId, data.CaptureId);
        Assert.AreEqual(64, data.BlueprintSha256.Length);
        Assert.IsFalse(Path.IsPathRooted(data.ProjectRelativeExportRoot), "IPC response must not expose an absolute project path.");

        var blocked = await handler.HandleAsync(new GenerateBlueprintCommand(
            "request-blueprint-raw",
            project.Id.ToString("D"),
            captureId,
            IncludeRawEvidenceReferences: true));
        Assert.IsFalse(blocked.Success);
        Assert.AreEqual("blueprint_policy_blocked", blocked.Code);
    }

    [TestMethod]
    public void BlueprintValidationRejectsUnbackedConclusionsAndUnvalidatedBehavioralModels()
    {
        var evidence = new EvidenceReference("EV-1", "state", "evidence/x", new string('a', 64), DateTimeOffset.UtcNow, EvidenceBasis.Observed);
        var unbacked = new ProjectBlueprint(
            ProjectBlueprint.CurrentSchemaVersion,
            Guid.NewGuid(),
            "Unbacked",
            "target",
            ProjectDomain.Web,
            DateTimeOffset.UtcNow,
            [evidence],
            [new ArchitectureElement("frontend", "Frontend", ArchitectureElementKind.Frontend, EvidenceBasis.Inferred, ConfidenceScore.FromPercent(50), [])],
            [], [], [], [], [],
            ReproductionReadiness.Calculate(0, 0, 0, 0, 0, 0, 0, false, false, false),
            [], []);
        Assert.ThrowsExactly<InvalidOperationException>(() => unbacked.Validate());

        var invalidModel = new BehavioralModel(
            "behavior-1", "Model", "Behavior", ["x"], ["y"],
            Experiments: 0,
            HoldoutCases: 0,
            HoldoutSimilarityPercent: 0,
            ConfidenceScore.FromPercent(0),
            [],
            ["EV-1"]);
        Assert.ThrowsExactly<InvalidOperationException>(() => invalidModel.Validate());
    }

    private static async Task WriteStateAsync(string root, string stateId, string url, string endpointKey, string href)
    {
        var stateDirectory = Path.Combine(root, "states", stateId);
        Directory.CreateDirectory(stateDirectory);
        await WriteJsonAsync(Path.Combine(stateDirectory, "state.json"), new
        {
            state = new
            {
                url,
                frame_path = Array.Empty<string>(),
                active_menu = Array.Empty<string>(),
                active_tabs = Array.Empty<string>(),
                open_modals = Array.Empty<string>(),
                filters = Array.Empty<object>(),
                pagination = Array.Empty<object>(),
                controls = Array.Empty<object>(),
                body_text = stateId,
                network_schema_keys = new[] { endpointKey }
            },
            controls = new[]
            {
                new
                {
                    frame_index = 0,
                    frame_url = url,
                    selector = "#details",
                    label = "Open details",
                    role = "button",
                    href,
                    element_type = "button",
                    semantic_kind = "button",
                    allowed = true,
                    reason = "read-only navigation"
                }
            }
        });
    }

    private static async Task WriteCaptureManifestAsync(string root, string captureId, string targetId)
    {
        var artifacts = new List<object>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetFileName(path), "capture-manifest.json", StringComparison.Ordinal))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(path);
            artifacts.Add(new
            {
                relative_path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                size_bytes = bytes.LongLength,
                media_type = Path.GetExtension(path) switch
                {
                    ".json" => "application/json",
                    ".md" => "text/markdown",
                    _ => "application/octet-stream"
                },
                classification = "sanitized"
            });
        }
        await WriteJsonAsync(Path.Combine(root, "capture-manifest.json"), new
        {
            schemaVersion = 1,
            captureId,
            targetId,
            createdAt = DateTimeOffset.UtcNow.ToString("O"),
            rawArtifactsInGit = false,
            artifacts
        });
    }

    private static Task WriteJsonAsync(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static string NetworkEndpointKey(string method, string url)
    {
        var uri = new Uri(url);
        var authority = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
        var canonical = $"{method.ToUpperInvariant()} {uri.Scheme.ToLowerInvariant()}://{authority.ToLowerInvariant()}{uri.AbsolutePath}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static AevrixDataPaths Paths(string root) => new(
        root,
        Path.Combine(root, "Projects"),
        Path.Combine(root, "Vault"),
        Path.Combine(root, "BrowserProfiles"),
        Path.Combine(root, "Engine"),
        Path.Combine(root, "Updates"),
        Path.Combine(root, "Logs"),
        Path.Combine(root, "Cache"));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
