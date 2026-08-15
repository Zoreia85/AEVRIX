using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record BlueprintExportResult(
    string RootPath,
    string ManifestPath,
    string BlueprintSha256,
    IReadOnlyList<string> GeneratedFiles);

public sealed class ProjectBlueprintExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<BlueprintExportResult> ExportAsync(
        ProjectBlueprint blueprint,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        blueprint.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);

        var directories = new[]
        {
            "00_MANIFEST",
            "01_ARCHITECTURE",
            "02_WORKFLOWS",
            "03_DATA_AND_API",
            "04_UI_AND_ASSETS",
            "05_CALCULATION_AND_ALGORITHMS",
            "06_OFFLINE_EVIDENCE",
            "07_COMPARISON",
            "08_REPORT"
        };

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(Path.Combine(root, directory));
        }

        var generated = new List<string>();
        var blueprintJson = JsonSerializer.Serialize(blueprint, JsonOptions);
        var blueprintPath = Path.Combine(root, "00_MANIFEST", "project-blueprint.json");
        await File.WriteAllTextAsync(blueprintPath, blueprintJson, new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, blueprintPath));

        var blueprintHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blueprintJson))).ToLowerInvariant();

        await WriteJsonAsync(root, "00_MANIFEST/evidence-index.json", blueprint.Evidence, generated, cancellationToken);
        await WriteJsonAsync(root, "00_MANIFEST/readiness.json", blueprint.Readiness, generated, cancellationToken);
        await WriteJsonAsync(root, "00_MANIFEST/limitations.json", blueprint.Limitations, generated, cancellationToken);
        await WriteJsonAsync(root, "00_MANIFEST/open-questions.json", blueprint.OpenQuestions, generated, cancellationToken);
        await WriteJsonAsync(root, "01_ARCHITECTURE/components.json", blueprint.ArchitectureElements, generated, cancellationToken);
        await WriteJsonAsync(root, "01_ARCHITECTURE/dependencies.json", blueprint.ArchitectureRelationships, generated, cancellationToken);
        await WriteJsonAsync(root, "02_WORKFLOWS/workflows.json", blueprint.Workflows, generated, cancellationToken);
        await WriteJsonAsync(root, "03_DATA_AND_API/endpoints.json", blueprint.ApiEndpoints, generated, cancellationToken);
        await WriteJsonAsync(root, "04_UI_AND_ASSETS/components.json", blueprint.UiComponents, generated, cancellationToken);
        await WriteJsonAsync(root, "05_CALCULATION_AND_ALGORITHMS/inferred-models.json", blueprint.BehavioralModels, generated, cancellationToken);

        var mermaid = BuildMermaid(blueprint);
        var mermaidPath = Path.Combine(root, "01_ARCHITECTURE", "architecture.mmd");
        await File.WriteAllTextAsync(mermaidPath, mermaid, new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, mermaidPath));

        var graphMl = BuildGraphMl(blueprint);
        var graphMlPath = Path.Combine(root, "01_ARCHITECTURE", "architecture.graphml");
        await File.WriteAllTextAsync(graphMlPath, graphMl, new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, graphMlPath));

        var workflowMermaid = BuildWorkflowMermaid(blueprint);
        var workflowPath = Path.Combine(root, "02_WORKFLOWS", "user-flows.mmd");
        await File.WriteAllTextAsync(workflowPath, workflowMermaid, new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, workflowPath));

        var summary = BuildSummary(blueprint, blueprintHash);
        var summaryPath = Path.Combine(root, "08_REPORT", "executive-summary.md");
        await File.WriteAllTextAsync(summaryPath, summary, new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, summaryPath));

        var manifest = new
        {
            schemaVersion = 1,
            blueprint.ProjectId,
            blueprint.ProjectName,
            blueprint.TargetId,
            domain = blueprint.Domain.ToString(),
            blueprint.GeneratedAt,
            blueprintSha256 = blueprintHash,
            files = generated.OrderBy(item => item, StringComparer.Ordinal).ToArray()
        };
        var manifestPath = Path.Combine(root, "00_MANIFEST", "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, manifestPath));

        return new BlueprintExportResult(root, manifestPath, blueprintHash, generated);
    }

    private static async Task WriteJsonAsync<T>(
        string root,
        string relativePath,
        T value,
        List<string> generated,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false), cancellationToken);
        generated.Add(Relative(root, path));
    }

    private static string BuildMermaid(ProjectBlueprint blueprint)
    {
        var builder = new StringBuilder("flowchart LR\n");
        foreach (var node in blueprint.ArchitectureElements)
        {
            builder.Append("  ").Append(NodeId(node.Id)).Append("[\"").Append(EscapeMermaid(node.Name)).Append("\"]")
                .Append(":::basis_").Append(node.Basis.ToString().ToLowerInvariant()).AppendLine();
        }

        foreach (var edge in blueprint.ArchitectureRelationships)
        {
            builder.Append("  ").Append(NodeId(edge.FromId)).Append(" -->|\"")
                .Append(EscapeMermaid(edge.Relationship)).Append("\"| ").Append(NodeId(edge.ToId)).AppendLine();
        }

        builder.AppendLine("  classDef basis_observed stroke-width:2px;")
            .AppendLine("  classDef basis_experimentallyvalidated stroke-dasharray:0;")
            .AppendLine("  classDef basis_inferred stroke-dasharray:5 5;")
            .AppendLine("  classDef basis_vendorclaim stroke-dasharray:2 4;");
        return builder.ToString();
    }

    private static string BuildWorkflowMermaid(ProjectBlueprint blueprint)
    {
        var builder = new StringBuilder();
        foreach (var workflow in blueprint.Workflows)
        {
            builder.Append("flowchart LR\n");
            for (var index = 0; index < workflow.Steps.Count; index++)
            {
                var step = workflow.Steps[index];
                builder.Append("  ").Append(NodeId(workflow.Id + "_" + step.Id)).Append("[\"")
                    .Append(EscapeMermaid(step.Label)).AppendLine("\"]");
                if (index > 0)
                {
                    var previous = workflow.Steps[index - 1];
                    builder.Append("  ").Append(NodeId(workflow.Id + "_" + previous.Id)).Append(" --> ")
                        .Append(NodeId(workflow.Id + "_" + step.Id)).AppendLine();
                }
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string BuildGraphMl(ProjectBlueprint blueprint)
    {
        static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>")
            .AppendLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">")
            .AppendLine("  <key id=\"name\" for=\"node\" attr.name=\"name\" attr.type=\"string\"/>")
            .AppendLine("  <key id=\"basis\" for=\"node\" attr.name=\"basis\" attr.type=\"string\"/>")
            .AppendLine("  <graph id=\"architecture\" edgedefault=\"directed\">");
        foreach (var node in blueprint.ArchitectureElements)
        {
            builder.Append("    <node id=\"").Append(Xml(node.Id)).Append("\"><data key=\"name\">")
                .Append(Xml(node.Name)).Append("</data><data key=\"basis\">").Append(node.Basis)
                .AppendLine("</data></node>");
        }
        var index = 0;
        foreach (var edge in blueprint.ArchitectureRelationships)
        {
            builder.Append("    <edge id=\"e").Append(index++).Append("\" source=\"").Append(Xml(edge.FromId))
                .Append("\" target=\"").Append(Xml(edge.ToId)).Append("\"/>").AppendLine();
        }
        builder.AppendLine("  </graph>").AppendLine("</graphml>");
        return builder.ToString();
    }

    private static string BuildSummary(ProjectBlueprint blueprint, string blueprintHash)
    {
        var readiness = blueprint.Readiness;
        return $"""
# AEVRIX Project Blueprint

**Project:** {blueprint.ProjectName}  
**Target:** {blueprint.TargetId}  
**Domain:** {blueprint.Domain}  
**Generated:** {blueprint.GeneratedAt:O}  
**Blueprint SHA-256:** `{blueprintHash}`

## Reproduction Readiness

**Overall:** {readiness.OverallPercent:0.00}% ({readiness.Grade})  
**Ready for independent rebuild:** {(readiness.ReadyForIndependentRebuild ? "YES" : "NO")}

{string.Join(Environment.NewLine, readiness.Dimensions.Select(item => $"- {item.Name}: {item.Percent:0.00}%"))}

## Inventory

- Evidence references: {blueprint.Evidence.Count}
- Architecture elements: {blueprint.ArchitectureElements.Count}
- Architecture relationships: {blueprint.ArchitectureRelationships.Count}
- Workflows: {blueprint.Workflows.Count}
- API endpoints: {blueprint.ApiEndpoints.Count}
- UI components: {blueprint.UiComponents.Count}
- Behavioral models: {blueprint.BehavioralModels.Count}

## Limitations

{string.Join(Environment.NewLine, blueprint.Limitations.Select(item => $"- {item}"))}

## Interpretation rule

Observed, experimentally validated, inferred and vendor-claimed information remain explicitly separated. AEVRIX does not label an independently inferred behavioral model as the target's internal proprietary algorithm.
""";
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string NodeId(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray();
        return "n_" + new string(chars);
    }

    private static string EscapeMermaid(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "'", StringComparison.Ordinal);
}
