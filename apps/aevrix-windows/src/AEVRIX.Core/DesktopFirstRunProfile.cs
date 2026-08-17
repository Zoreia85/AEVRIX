using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevrix.Core;

public sealed record DesktopFirstRunProfile(
    int SchemaVersion,
    string InstallationId,
    DesktopOperatingMode? RequestedMode,
    bool PermissionsAcknowledged,
    string? DeviceCertificateThumbprint,
    string? RemoteBaseUri,
    DateTimeOffset? CompletedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public static DesktopFirstRunProfile CreateNew() =>
        new(
            CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            RequestedMode: null,
            PermissionsAcknowledged: false,
            DeviceCertificateThumbprint: null,
            RemoteBaseUri: null,
            CompletedAtUtc: null);

    public DesktopFirstRunProfile Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported desktop first-run schema version: {SchemaVersion}.");
        }

        var installationId = InstallationId.Trim().ToLowerInvariant();
        if (installationId.Length is < 16 or > 64
            || installationId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
        {
            throw new InvalidDataException("Desktop first-run installation id is invalid.");
        }

        if (RequestedMode is not null && !Enum.IsDefined(RequestedMode.Value))
        {
            throw new InvalidDataException("Desktop first-run operating mode is invalid.");
        }

        if (!string.IsNullOrWhiteSpace(DeviceCertificateThumbprint))
        {
            var normalizedThumbprint = new string(DeviceCertificateThumbprint.Where(Uri.IsHexDigit).ToArray());
            if (normalizedThumbprint.Length is < 40 or > 128)
            {
                throw new InvalidDataException("Desktop first-run device certificate thumbprint is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(RemoteBaseUri))
        {
            if (!Uri.TryCreate(RemoteBaseUri.Trim(), UriKind.Absolute, out var remoteUri)
                || remoteUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(remoteUri.UserInfo)
                || !string.IsNullOrEmpty(remoteUri.Query)
                || !string.IsNullOrEmpty(remoteUri.Fragment))
            {
                throw new InvalidDataException("Desktop first-run remote base URI must be canonical HTTPS without credentials, query or fragment.");
            }
        }

        if (CompletedAtUtc is not null && (RequestedMode is null || !PermissionsAcknowledged))
        {
            throw new InvalidDataException("Completed desktop first-run state is inconsistent with required acknowledgements.");
        }

        return this with { InstallationId = installationId };
    }
}

public sealed class DesktopFirstRunProfileStore
{
    private const int MaximumProfileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public DesktopFirstRunProfileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static DesktopFirstRunProfileStore ForCurrentUser()
    {
        var dataPaths = AevrixDataPaths.ForCurrentUser().EnsureCreated();
        return new DesktopFirstRunProfileStore(System.IO.Path.Combine(dataPaths.UserRoot, "desktop-first-run.json"));
    }

    public DesktopFirstRunProfile LoadOrCreate()
    {
        if (!File.Exists(Path))
        {
            var created = DesktopFirstRunProfile.CreateNew().Validate();
            Save(created);
            return created;
        }

        var info = new FileInfo(Path);
        if (info.Length is <= 0 or > MaximumProfileBytes)
        {
            throw new InvalidDataException("Desktop first-run profile size is invalid.");
        }

        var json = File.ReadAllText(Path, Encoding.UTF8);
        DesktopFirstRunProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<DesktopFirstRunProfile>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Desktop first-run profile JSON is invalid.", exception);
        }

        return (profile ?? throw new InvalidDataException("Desktop first-run profile is empty.")).Validate();
    }

    public void Save(DesktopFirstRunProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validated = profile.Validate();
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Desktop first-run profile path has no parent directory.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(validated, JsonOptions);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes <= 0 || bytes > MaximumProfileBytes)
        {
            throw new InvalidDataException("Desktop first-run profile serialization exceeded the allowed size.");
        }

        var tempPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, Path, overwrite: true);
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
