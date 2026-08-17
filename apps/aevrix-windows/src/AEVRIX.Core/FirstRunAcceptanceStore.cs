using System.Text.Json;

namespace Aevrix.Core;

public sealed record FirstRunAcceptance(
    int SchemaVersion,
    string TermsRevision,
    DateTimeOffset AcceptedAtUtc);

public sealed record FirstRunPresentation(
    int SchemaVersion,
    string TermsRevision,
    DateTimeOffset PresentedAtUtc);

/// <summary>
/// Persists only the local product first-run acknowledgement. This state never grants
/// mission authorization, remote authentication, execution authority, or security policy approval.
/// Missing, stale, malformed, or unreadable state is always treated as not accepted.
/// </summary>
public sealed class FirstRunAcceptanceStore
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentTermsRevision = "preview-authorized-use-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _rootDirectory;

    public FirstRunAcceptanceStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("First-run state root is required.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string AcceptancePath => Path.Combine(_rootDirectory, "first-run-acceptance.json");

    public string PresentationPath => Path.Combine(_rootDirectory, "first-run-presentation.json");

    public bool IsAccepted()
    {
        try
        {
            if (!File.Exists(AcceptancePath))
            {
                return false;
            }

            var acceptance = JsonSerializer.Deserialize<FirstRunAcceptance>(
                File.ReadAllText(AcceptancePath),
                JsonOptions);

            return acceptance is not null
                && acceptance.SchemaVersion == CurrentSchemaVersion
                && string.Equals(acceptance.TermsRevision, CurrentTermsRevision, StringComparison.Ordinal)
                && acceptance.AcceptedAtUtc != default;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public FirstRunAcceptance Accept(DateTimeOffset? acceptedAtUtc = null)
    {
        var acceptance = new FirstRunAcceptance(
            CurrentSchemaVersion,
            CurrentTermsRevision,
            acceptedAtUtc ?? DateTimeOffset.UtcNow);

        WriteAtomically(AcceptancePath, acceptance);
        return acceptance;
    }

    public FirstRunPresentation RecordPresentation(DateTimeOffset? presentedAtUtc = null)
    {
        var presentation = new FirstRunPresentation(
            CurrentSchemaVersion,
            CurrentTermsRevision,
            presentedAtUtc ?? DateTimeOffset.UtcNow);

        WriteAtomically(PresentationPath, presentation);
        return presentation;
    }

    private void WriteAtomically<T>(string destinationPath, T value)
    {
        Directory.CreateDirectory(_rootDirectory);
        var tempPath = Path.Combine(_rootDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
