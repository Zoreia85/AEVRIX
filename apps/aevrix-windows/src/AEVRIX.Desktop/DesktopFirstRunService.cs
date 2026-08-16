using System.Security.Cryptography;
using System.Text.Json;
using Aevrix.Core;

namespace AEVRIX.Desktop;

internal sealed record DesktopFirstRunIdentityState(
    bool Verified,
    string State,
    string Detail,
    string? InstallationId = null,
    string? KeyId = null,
    string? SecurityTier = null,
    DateTimeOffset? PreparedAt = null);

internal sealed record DesktopFirstRunIdentityMetadata(
    int SchemaVersion,
    string InstallationId,
    string KeyId,
    string SecurityTier,
    DateTimeOffset PreparedAt);

/// <summary>
/// Owns only Desktop first-run metadata. Device private keys remain non-exportable in Windows CNG/TPM.
/// Remote enrollment is deliberately outside this service and is never inferred from local preparation.
/// </summary>
internal sealed class DesktopFirstRunService
{
    private const int CurrentSchemaVersion = 1;
    private readonly AevrixDataPaths _paths;
    private readonly string _metadataPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DesktopFirstRunService(AevrixDataPaths? paths = null)
    {
        _paths = paths ?? AevrixDataPaths.ForCurrentUser();
        _metadataPath = Path.Combine(_paths.UserRoot, "desktop-first-run.json");
    }

    public async Task<DesktopFirstRunIdentityState> ReadLocalStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_metadataPath))
        {
            return new DesktopFirstRunIdentityState(
                false,
                "Não preparada",
                "Nenhuma identidade local de dispositivo foi registrada por esta instalação.");
        }

        try
        {
            var metadata = await ReadMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is null || metadata.SchemaVersion != CurrentSchemaVersion)
            {
                return new DesktopFirstRunIdentityState(
                    false,
                    "Bloqueada",
                    "Os metadados locais de primeira execução não puderam ser validados.");
            }

            return new DesktopFirstRunIdentityState(
                false,
                "Preparada — verificar",
                "Metadados locais existem, mas a chave TPM ainda não foi revalidada nesta sessão.",
                metadata.InstallationId,
                metadata.KeyId,
                metadata.SecurityTier,
                metadata.PreparedAt);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            return new DesktopFirstRunIdentityState(
                false,
                "Bloqueada",
                $"O estado local de primeira execução foi rejeitado ({ex.GetType().Name}).");
        }
    }

    public async Task<DesktopFirstRunIdentityState> PrepareOrVerifyTpmIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DesktopFirstRunIdentityState(
                false,
                "Bloqueada",
                "A identidade de dispositivo AEVRIX requer Windows CNG/TPM.");
        }

        try
        {
            _paths.EnsureCreated();
            var existing = File.Exists(_metadataPath)
                ? await ReadMetadataAsync(cancellationToken).ConfigureAwait(false)
                : null;

            if (existing is not null && existing.SchemaVersion != CurrentSchemaVersion)
            {
                return new DesktopFirstRunIdentityState(
                    false,
                    "Bloqueada",
                    "A versão dos metadados locais é incompatível; nenhum material foi sobrescrito.");
            }

            var installationId = existing?.InstallationId
                ?? $"install-{Guid.NewGuid():N}";

            var provisioner = new WindowsDeviceIdentityProvisioner();
            using var key = provisioner.GetOrCreateTpmKey(installationId);
            var enrollment = provisioner.CreateEnrollmentMaterial(key);

            if (existing is not null
                && (!string.Equals(existing.KeyId, enrollment.KeyId, StringComparison.Ordinal)
                    || !string.Equals(
                        existing.SecurityTier,
                        enrollment.SecurityTier.ToString(),
                        StringComparison.Ordinal)))
            {
                return new DesktopFirstRunIdentityState(
                    false,
                    "Bloqueada",
                    "A identidade TPM atual não corresponde aos metadados registrados. Nenhum enrollment remoto foi tentado.");
            }

            var preparedAt = existing?.PreparedAt ?? DateTimeOffset.UtcNow;
            var metadata = new DesktopFirstRunIdentityMetadata(
                CurrentSchemaVersion,
                enrollment.InstallationId,
                enrollment.KeyId,
                enrollment.SecurityTier.ToString(),
                preparedAt);

            await WriteMetadataAtomicallyAsync(metadata, cancellationToken).ConfigureAwait(false);

            return new DesktopFirstRunIdentityState(
                true,
                "TPM verificado",
                "Chave ECDSA P-256 não exportável validada no TPM. Enrollment remoto ainda está pendente.",
                metadata.InstallationId,
                metadata.KeyId,
                metadata.SecurityTier,
                metadata.PreparedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            return new DesktopFirstRunIdentityState(
                false,
                "Bloqueada",
                $"A identidade TPM não pôde ser preparada ou verificada ({ex.GetType().Name}). Não houve fallback automático para chave em software.");
        }
    }

    private async Task<DesktopFirstRunIdentityMetadata?> ReadMetadataAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _metadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<DesktopFirstRunIdentityMetadata>(
            stream,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteMetadataAtomicallyAsync(
        DesktopFirstRunIdentityMetadata metadata,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UserRoot);
        var tempPath = _metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    metadata,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _metadataPath, overwrite: true);
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
