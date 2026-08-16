using Aevrix.Remote.Orchestration;

namespace Aevrix.Remote.Orchestration.Tests;

internal static class TestProofBoundMissionDirector
{
    public static MissionDirector Create(
        IEnumerable<IMissionSpecialist> specialists,
        TimeProvider? timeProvider = null) =>
        MissionDirector.CreateProofBound(
            specialists,
            new ExecutionProofJournalRegistry(new MemoryExecutionProofStore()),
            timeProvider,
            new ProofRecordingMissionSpecialistOptions(TimeSpan.FromSeconds(2)));

    private sealed class MemoryExecutionProofStore : IExecutionProofStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, StoredExecutionProofSnapshot> _snapshots = [];

        public Task<StoredExecutionProofSnapshot?> LoadAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!_snapshots.TryGetValue(projectId, out var snapshot))
                    return Task.FromResult<StoredExecutionProofSnapshot?>(null);

                return Task.FromResult<StoredExecutionProofSnapshot?>(
                    new StoredExecutionProofSnapshot(
                        snapshot.ProjectId,
                        snapshot.Records.ToArray(),
                        snapshot.Head));
            }
        }

        public Task SaveAsync(
            Guid projectId,
            IReadOnlyList<ExecutionProofRecord> records,
            ExecutionProofHead head,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionProofLedger.VerifySnapshot(records, head);
            if (records.Any(record => record.Event.ProjectId != projectId))
                throw new InvalidDataException("Test proof store rejected cross-project records.");

            lock (_sync)
            {
                _snapshots[projectId] = new StoredExecutionProofSnapshot(
                    projectId,
                    records.ToArray(),
                    head);
            }

            return Task.CompletedTask;
        }
    }
}
