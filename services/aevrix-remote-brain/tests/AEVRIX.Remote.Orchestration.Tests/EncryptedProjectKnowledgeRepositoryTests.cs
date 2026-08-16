using System.Security.Cryptography;
using System.Text;
using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class EncryptedProjectKnowledgeRepositoryTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task Candidate_RoundTripsEncryptedWithoutPlaintextAtRest()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();

        await repository.StoreCandidateAsync(candidate);
        var loaded = await repository.LoadAsync(candidate.KnowledgeId);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(candidate.Statement, loaded.Statement);
        Assert.AreEqual(candidate.ProjectId, loaded.ProjectId);
        var vaultBytes = Directory.GetFiles(temp.Path, "*.aevk", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();
        var plaintext = Encoding.UTF8.GetBytes(candidate.Statement);
        Assert.IsFalse(ContainsSequence(vaultBytes, plaintext));
    }

    [TestMethod]
    public async Task Candidate_IdIsImmutable()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.StoreCandidateAsync(candidate with { Statement = "forged replacement" }));
    }

    [TestMethod]
    public async Task Load_FailsClosedAfterCiphertextTampering()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);
        var file = Directory.GetFiles(temp.Path, "*.aevk", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^8] ^= 0x55;
        await File.WriteAllBytesAsync(file, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync(candidate.KnowledgeId));
    }

    [TestMethod]
    public async Task Promote_RequiresStoredAuthoritativeValidationAndMatchingState()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);
        var validation = PassingValidation(candidate);
        await repository.StoreValidationAsync(validation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.PromoteAsync(
            candidate.KnowledgeId,
            KnowledgeTrustState.Validated,
            validation.ValidationRecordId,
            new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero)));

        await repository.PromoteAsync(
            candidate.KnowledgeId,
            KnowledgeTrustState.Trusted,
            validation.ValidationRecordId,
            new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero));

        var promoted = await repository.LoadAsync(candidate.KnowledgeId);
        Assert.IsNotNull(promoted);
        Assert.AreEqual(KnowledgeTrustState.Trusted, promoted.TrustState);
        Assert.AreEqual(validation.ValidationRecordId, promoted.ValidationRecordId);
    }

    [TestMethod]
    public async Task Validation_CannotReferenceEvidenceOutsideCandidateBoundary()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);
        var validation = PassingValidation(candidate) with
        {
            ValidatedEvidenceIds = ["obs-framework-a", "obs-forged"]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StoreValidationAsync(validation));
    }

    [TestMethod]
    public async Task ProjectKey_MustBeExactly256Bits()
    {
        using var temp = new TempDirectory();
        var repository = new EncryptedProjectKnowledgeRepository(temp.Path, new InvalidKeyProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StoreCandidateAsync(Candidate()));
    }

    private static EncryptedProjectKnowledgeRepository Repository(string root) =>
        new(root, new DeterministicProjectKeyProvider());

    private static CandidateKnowledge Candidate() => new(
        KnowledgeId: "KN-vault-0123456789abcdef",
        ProjectId: ProjectId,
        TargetId: "target-001",
        Statement: "framework = ASP.NET",
        TrustState: KnowledgeTrustState.Candidate,
        Confidence: 0.96,
        Risk: ModelRiskLevel.Low,
        EvidenceIds: ["obs-framework-a", "obs-framework-b"],
        ProviderTrace: ["evidence-fusion@fusion-v1:0.960:Low"],
        Assumptions: [],
        OpenQuestions: [],
        CreatedAt: new DateTimeOffset(2026, 8, 14, 21, 50, 0, TimeSpan.Zero),
        UpdatedAt: new DateTimeOffset(2026, 8, 14, 21, 50, 0, TimeSpan.Zero));

    private static KnowledgeValidationRecord PassingValidation(CandidateKnowledge candidate) => new(
        ValidationRecordId: "VR-vault-0123456789abcdef",
        KnowledgeId: candidate.KnowledgeId,
        EvidenceIntegrityPassed: true,
        EvidenceSupportsStatement: true,
        IndependentValidationPassed: true,
        CounterexampleReviewPassed: true,
        ValidatedEvidenceIds: candidate.EvidenceIds,
        Counterexamples: [],
        ValidatedAt: new DateTimeOffset(2026, 8, 14, 21, 55, 0, TimeSpan.Zero));

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class DeterministicProjectKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = SHA256.HashData(Encoding.UTF8.GetBytes("test-key:" + projectId.ToString("D")));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(key);
        }
    }

    private sealed class InvalidKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[16]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aevrix-vault-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort test cleanup only.
            }
        }
    }
}
