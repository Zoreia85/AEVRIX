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
        var vaultBytes = Directory.GetFiles(temp.Path, "*.aevk", SearchOption.AllDirectories).SelectMany(File.ReadAllBytes).ToArray();
        Assert.IsFalse(ContainsSequence(vaultBytes, Encoding.UTF8.GetBytes(candidate.Statement)));
    }

    [TestMethod]
    public async Task Candidate_IdIsImmutable()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.StoreCandidateAsync(candidate with { Statement = "forged replacement" }));
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
    public async Task GenericValidationOutcomeCannotCreateTrustedState()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);
        var validation = PassingValidation(candidate);
        await repository.StoreValidationAsync(validation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ApplyValidationOutcomeAsync(
            candidate.KnowledgeId,
            KnowledgeTrustState.Trusted,
            validation.ValidationRecordId,
            new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ApplyValidationOutcomeAsync(
            candidate.KnowledgeId,
            KnowledgeTrustState.Validated,
            validation.ValidationRecordId,
            new DateTimeOffset(2026, 8, 14, 22, 0, 0, TimeSpan.Zero)));

        Assert.AreEqual(KnowledgeTrustState.Candidate, (await repository.LoadAsync(candidate.KnowledgeId))!.TrustState);
    }

    [TestMethod]
    public async Task Validation_CannotReferenceEvidenceOutsideCandidateBoundary()
    {
        using var temp = new TempDirectory();
        var repository = Repository(temp.Path);
        var candidate = Candidate();
        await repository.StoreCandidateAsync(candidate);
        var validation = PassingValidation(candidate) with { ValidatedEvidenceIds = ["obs-framework-a", "obs-forged"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StoreValidationAsync(validation));
    }

    [TestMethod]
    public async Task ProjectKey_MustBeExactly256Bits()
    {
        using var temp = new TempDirectory();
        var repository = new EncryptedProjectKnowledgeRepository(temp.Path, new InvalidKeyProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.StoreCandidateAsync(Candidate()));
    }

    private static EncryptedProjectKnowledgeRepository Repository(string root) => new(root, new DeterministicProjectKeyProvider());

    private static CandidateKnowledge Candidate() => new(
        "KN-vault-0123456789abcdef", ProjectId, "target-001", "framework = ASP.NET",
        KnowledgeTrustState.Candidate, 0.96, ModelRiskLevel.Low,
        ["obs-framework-a", "obs-framework-b"], ["evidence-fusion@fusion-v1:0.960:Low"], [], [],
        new DateTimeOffset(2026, 8, 14, 21, 50, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 14, 21, 50, 0, TimeSpan.Zero));

    private static KnowledgeValidationRecord PassingValidation(CandidateKnowledge candidate) => new(
        "VR-vault-0123456789abcdef", candidate.KnowledgeId, true, true, true, true,
        candidate.EvidenceIds, [], new DateTimeOffset(2026, 8, 14, 21, 55, 0, TimeSpan.Zero));

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    private sealed class DeterministicProjectKeyProvider : IProjectKnowledgeKeyProvider
    {
        public ValueTask<ReadOnlyMemory<byte>> GetKeyAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(SHA256.HashData(Encoding.UTF8.GetBytes("test-key:" + projectId.ToString("D"))));
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
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
