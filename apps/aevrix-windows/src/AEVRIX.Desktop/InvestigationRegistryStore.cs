using System.Text.Json;
using Aevrix.Core;

namespace AEVRIX.Desktop;

internal sealed record InvestigationRegistryEntry(
    Guid Id,
    string Workspace,
    InvestigationTargetKind TargetKind,
    InvestigationStrategy Strategy,
    InvestigationRunState State,
    InvestigationPhase CurrentPhase,
    double PercentComplete,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc,
    string? EstimatedRemaining,
    string? Blocker);

internal sealed class InvestigationRegistryStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private InvestigationRegistryStore(string path)
    {
        _path = path;
    }

    public static InvestigationRegistryStore ForCurrentUser()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AEVRIX",
            "UserData");
        Directory.CreateDirectory(root);
        return new InvestigationRegistryStore(Path.Combine(root, "investigations.json"));
    }

    public IReadOnlyList<InvestigationRegistryEntry> Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<InvestigationRegistryEntry>();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<InvestigationRegistryEntry>>(json, _jsonOptions);
            return entries is null
                ? Array.Empty<InvestigationRegistryEntry>()
                : entries;
        }
        catch (JsonException)
        {
            return Array.Empty<InvestigationRegistryEntry>();
        }
    }

    public InvestigationRegistryEntry AddDraft(InvestigationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var entries = Load().ToList();
        var now = DateTimeOffset.UtcNow;
        var entry = new InvestigationRegistryEntry(
            draft.Id,
            draft.Workspace,
            draft.TargetKind,
            draft.Strategy,
            InvestigationRunState.Draft,
            InvestigationPhase.IntakeAndAuthorization,
            0,
            draft.CreatedAtUtc,
            now,
            null,
            "Aguardando vínculo com o motor de políticas e o orquestrador para iniciar execução.");
        entries.Insert(0, entry);
        Save(entries);
        return entry;
    }

    private void Save(IReadOnlyCollection<InvestigationRegistryEntry> entries)
    {
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, _jsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }
}
