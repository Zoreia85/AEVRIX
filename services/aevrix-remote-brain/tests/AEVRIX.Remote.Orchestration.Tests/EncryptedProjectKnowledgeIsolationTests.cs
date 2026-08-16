using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class EncryptedProjectKnowledgeIsolationTests
{
    [TestMethod]
    public async Task ValidationId_CannotBeReusedAcrossProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "aevrix-vault-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new EncryptedProjectKnowledgeRepository(root, new PerProjectKeyProvider());
            var projectA = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var projectB = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var candidateB = Candidate("KN-project-b", projectB, "obs-b");
            await repository.StoreCandidateAsync(candidateB);
            await repository.StoreValidationAsync(Validation("VR-shared-id", candidateB));

            var candidateA = Candidate("KN-project-a", projectA, "obs-a");
            await repository.StoreCandidateAsync(candidateA);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                repository.StoreValidationAsync(Validation("VR-shared-id", candidateA)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CandidateKnowledge Candidate(string id, Guid projectId, string evidenceId) => new(
        id,
        projectId,
        "target-001",
        "runtime = .NET",
        KnowledgeTrustState.Candidate,
        0.95,
        ModelRiskLevel.Low,
        [evidenceId],
        ["provider@test"],
        [],
        [],
        new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero));

    private static KnowledgeValidationRecord Validation(string id, CandidateKnowledge candidate) => new(
        id,
        candidate.KnowledgeId,
        true,
        true,
        true,
        true,
        candidate.EvidenceIds,
        [],
        new DateTimeOffset(2026, 8, 14, 22, 1, 0, TimeSpan.Zero));

    private sealed class PerProjectKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                SHA256.HashData(Encoding.UTF8.GetBytes("project-key:" + projectId.ToString("D"))));
        }
    }
}
