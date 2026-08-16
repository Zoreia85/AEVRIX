using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Canonical identity derivation shared by mission execution and downstream provenance verification.
/// Keeping this in one primitive prevents Blueprint verification from drifting from proof recording.
/// </summary>
public static class MissionExecutionProofIdentity
{
    public static string CreateExecutionId(
        Guid projectId,
        string missionId,
        string targetId,
        string taskId,
        MissionSpecialistKind specialist)
    {
        if (projectId == Guid.Empty
            || !MissionTaskSpec.IsSafeId(missionId, 3, 128)
            || !MissionTaskSpec.IsSafeId(targetId, 2, 128)
            || !MissionTaskSpec.IsSafeId(taskId, 3, 128)
            || !Enum.IsDefined(specialist))
        {
            throw new ArgumentException("Mission execution proof identity scope is invalid.");
        }

        var digest = Digest([
            "aevrix-mission-execution-v1",
            projectId.ToString("D"),
            missionId,
            targetId,
            taskId,
            specialist.ToString()
        ]);
        return "mission-task:" + digest;
    }

    private static string Digest(IEnumerable<string> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part ?? string.Empty);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
