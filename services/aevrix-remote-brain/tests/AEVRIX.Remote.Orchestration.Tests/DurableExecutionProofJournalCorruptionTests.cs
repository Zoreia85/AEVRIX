using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

[TestClass]
public sealed class DurableExecutionProofJournalCorruptionTests
{
    [TestMethod]
    public async Task Open_RejectsNullPersistedRecordAsInvalidData()
    {
        var projectId = Guid.Parse("71717171-7171-7171-7171-717171717171");
        var store = new NullRecordStore(projectId);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            DurableExecutionProofJournal.OpenAsync(projectId, store));
    }

    private sealed class NullRecordStore(Guid projectId) : IExecutionProofStore
    {
        public Task<StoredExecutionProofSnapshot?> LoadAsync(
            Guid requestedProjectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual(projectId, requestedProjectId);
            IReadOnlyList<ExecutionProofRecord> records = new ExecutionProofRecord[] { null! };
            return Task.FromResult<StoredExecutionProofSnapshot?>(
                new StoredExecutionProofSnapshot(
                    projectId,
                    records,
                    new ExecutionProofHead(1, new string('a', 64))));
        }

        public Task SaveAsync(
            Guid requestedProjectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
