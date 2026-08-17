using System.Security.Cryptography;

namespace Aevrix.Core;

public sealed record DesktopIntegrityArtifact(
    string Role,
    string FileName,
    long Bytes,
    string Sha256);

public sealed record DesktopLocalIntegrityResult(
    bool Verified,
    string Detail,
    IReadOnlyList<DesktopIntegrityArtifact> Artifacts);

public static class DesktopLocalIntegrityProbe
{
    public static DesktopLocalIntegrityResult Probe(params (string Role, string Path)[] requiredFiles)
    {
        ArgumentNullException.ThrowIfNull(requiredFiles);
        if (requiredFiles.Length is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFiles), "Between 1 and 16 required files must be supplied.");
        }

        var artifacts = new List<DesktopIntegrityArtifact>(requiredFiles.Length);
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (role, path) in requiredFiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(role);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var fullPath = System.IO.Path.GetFullPath(path);
            if (!normalizedPaths.Add(fullPath))
            {
                return new DesktopLocalIntegrityResult(
                    false,
                    $"O mesmo arquivo foi apresentado para mais de um papel obrigatório ({System.IO.Path.GetFileName(fullPath)}).",
                    artifacts);
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length <= 0)
            {
                return new DesktopLocalIntegrityResult(
                    false,
                    $"Arquivo obrigatório ausente ou vazio: {role}.",
                    artifacts);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new DesktopLocalIntegrityResult(
                    false,
                    $"Arquivo obrigatório usa redirecionamento/reparse point e foi rejeitado: {role}.",
                    artifacts);
            }

            string sha256;
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            artifacts.Add(new DesktopIntegrityArtifact(
                role.Trim(),
                info.Name,
                info.Length,
                sha256));
        }

        return new DesktopLocalIntegrityResult(
            true,
            "Estrutura local obrigatória verificada. Hashes foram calculados para diagnóstico; esta prova não substitui Authenticode ou manifesto de release assinado.",
            artifacts);
    }
}
