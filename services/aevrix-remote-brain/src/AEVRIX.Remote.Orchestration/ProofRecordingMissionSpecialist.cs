using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aevrix.Remote.Orchestration;

/// <summary>
/// Resolves the single in-process durable execution journal for a project. Implementations must not
/// return independent journals for concurrent calls targeting the same project because doing so
/// would create competing local views of one externally anchored ledger.
/// </summary>
public interface IExecutionProofJournalProvider
{
    Task<DurableExecutionProofJournal> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local single-flight registry over one project-aware proof store. The durable store and
/// its external anchor remain the authority across process restarts; this registry only prevents
/// multiple competing journal objects inside the same process.
/// </summary>
public sealed class ExecutionProofJournalRegistry : IExecutionProofJournalProvider
{
    private readonly IExecutionProofStore _store;
    private readonly ConcurrentDictionary<Guid, DurableExecutionProofJournal> _journals = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _openGates = new();

    public ExecutionProofJournalRegistry(IExecutionProofStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<DurableExecutionProofJournal> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (projectId == Guid.Empty)
            throw new ArgumentException("Execution proof project id cannot be empty.", nameof(projectId));

        if (_journals.TryGetValue(projectId, out var existing))
            return existing;

        var gate = _openGates.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_journals.TryGetValue(projectId, out existing))
                return existing;

            var opened = await DurableExecutionProofJournal
                .OpenAsync(projectId, _store, cancellationToken)
                .ConfigureAwait(false);
            if (_journals.TryAdd(projectId, opened))
                return opened;

            return _journals[projectId];
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed record ProofRecordingMissionSpecialistOptions(TimeSpan FinalizationTimeout)
{
    public static ProofRecordingMissionSpecialistOptions Default { get; } =
        new(TimeSpan.FromSeconds(15));

    public ProofRecordingMissionSpecialistOptions Validate()
    {
        if (FinalizationTimeout < TimeSpan.FromSeconds(1)
            || FinalizationTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FinalizationTimeout),
                "Proof finalization timeout must be between one second and two minutes.");
        }

        return this;
    }
}

/// <summary>
/// Mission-specialist decorator that makes durable execution proof part of the execution boundary.
/// A specialist is never invoked until Started is durably accepted. A successful output is never
/// returned until Completed is durably accepted. Raw objective, summary, exception message and
/// artifact contents never enter the ledger; only deterministic SHA-256 digests and bounded ids do.
/// A pre-existing Started event is never treated as permission to replay work after a process loss;
/// that ambiguous state requires a separate reconciliation/lease decision.
/// </summary>
public sealed class ProofRecordingMissionSpecialist : IMissionSpecialist
{
    private const string CapabilityClass = "mission-specialist";

    private readonly IMissionSpecialist _inner;
    private readonly IExecutionProofJournalProvider _journals;
    private readonly TimeProvider _time;
    private readonly ProofRecordingMissionSpecialistOptions _options;

    public ProofRecordingMissionSpecialist(
        IMissionSpecialist inner,
        IExecutionProofJournalProvider journals,
        TimeProvider? timeProvider = null,
        ProofRecordingMissionSpecialistOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _time = timeProvider ?? TimeProvider.System;
        _options = (options ?? ProofRecordingMissionSpecialistOptions.Default).Validate();
    }

    public MissionSpecialistKind Kind => _inner.Kind;

    public async Task<SpecialistExecutionOutput> ExecuteAsync(
        SpecialistExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        cancellationToken.ThrowIfCancellationRequested();

        var identity = ExecutionIdentity.Create(context, Kind);
        var journal = await _journals.GetAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);
        await ClaimExecutionStartAsync(journal, identity, cancellationToken).ConfigureAwait(false);

        // Cancellation after a durable Started claim must close the proof before returning control;
        // otherwise a benign cancellation would leave an ambiguous started-only execution behind.
        if (cancellationToken.IsCancellationRequested)
        {
            await PersistTerminalAsync(
                journal,
                identity.CreateFailed(typeof(OperationCanceledException), _time.GetUtcNow())).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        SpecialistExecutionOutput output;
        try
        {
            output = (await _inner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false)).Validate();
            EnsureEvidenceBoundary(context.Task, output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await PersistTerminalAsync(
                journal,
                identity.CreateFailed(ex.GetType(), _time.GetUtcNow())).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PersistTerminalAsync(
                journal,
                identity.CreateFailed(typeof(OperationCanceledException), _time.GetUtcNow())).ConfigureAwait(false);
            throw;
        }

        // Terminal proof persistence is deliberately outside the specialist/output catch blocks.
        // If this save fails, the journal retains the exact successful candidate and the caller sees
        // the persistence failure. We must never append a contradictory Failed terminal event after
        // a successful Completed candidate may already have reached durable storage.
        await PersistTerminalAsync(
            journal,
            identity.CreateSucceeded(output, _time.GetUtcNow())).ConfigureAwait(false);
        return output;
    }

    private async Task ClaimExecutionStartAsync(
        DurableExecutionProofJournal journal,
        ExecutionIdentity identity,
        CancellationToken cancellationToken)
    {
        if (journal.HasPendingRecovery)
        {
            throw new InvalidOperationException(
                "Execution proof journal has unresolved recovery state; specialist execution is blocked until reconciliation completes.");
        }

        var existing = journal.Snapshot()
            .Where(record => string.Equals(
                record.Event.ExecutionId,
                identity.ExecutionId,
                StringComparison.Ordinal))
            .ToArray();
        if (existing.Length != 0)
        {
            throw new InvalidOperationException(
                "Mission specialist execution id already exists in the proof ledger; automatic replay is forbidden.");
        }

        var started = identity.CreateStarted(_time.GetUtcNow());
        try
        {
            await journal.AppendAndPersistAsync(started, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!journal.HasPendingRecovery)
                throw;

            // This recovery is safe because the pending candidate was created by the exact Started
            // append immediately above in this same call. We never use this path to resume an
            // arbitrary started-only execution discovered after restart.
            using var recovery = new CancellationTokenSource(_options.FinalizationTimeout);
            await journal.RecoverPendingAsync(recovery.Token).ConfigureAwait(false);
            var recovered = journal.Snapshot()
                .Where(record => string.Equals(
                    record.Event.ExecutionId,
                    identity.ExecutionId,
                    StringComparison.Ordinal))
                .ToArray();
            if (recovered.Length != 1 || recovered[0].Event != started)
            {
                throw new InvalidDataException(
                    "Execution proof recovery did not reproduce the exact Started claim; specialist execution remains blocked.");
            }
        }
    }

    private async Task PersistTerminalAsync(
        DurableExecutionProofJournal journal,
        ExecutionProofEvent terminal)
    {
        using var finalization = new CancellationTokenSource(_options.FinalizationTimeout);
        await journal.AppendAndPersistAsync(terminal, finalization.Token).ConfigureAwait(false);
    }

    private void ValidateContext(SpecialistExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Task.Validate();
        if (context.ProjectId == Guid.Empty)
            throw new InvalidDataException("Specialist execution project id cannot be empty.");
        if (!MissionTaskSpec.IsSafeId(context.MissionId, 3, 128)
            || !MissionTaskSpec.IsSafeId(context.TargetId, 2, 128))
        {
            throw new InvalidDataException("Specialist execution mission or target id is invalid.");
        }
        if (context.Task.Specialist != Kind)
            throw new InvalidDataException("Specialist execution context kind does not match the decorated specialist.");

        var dependencies = context.Task.DependsOn.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (context.DependencyResults.Count != dependencies.Count
            || context.DependencyResults.Keys.Any(key => !dependencies.Contains(key)))
        {
            throw new InvalidDataException("Specialist execution dependency result boundary is inconsistent with the task.");
        }

        foreach (var pair in context.DependencyResults)
        {
            if (!string.Equals(pair.Key, pair.Value.TaskId, StringComparison.OrdinalIgnoreCase)
                || pair.Value.State != MissionTaskState.Succeeded)
            {
                throw new InvalidDataException("Specialist execution dependency results are not successful and self-consistent.");
            }
        }
    }

    private static void EnsureEvidenceBoundary(
        MissionTaskSpec task,
        SpecialistExecutionOutput output)
    {
        if (output.EvidenceIds.Except(task.EvidenceIds, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidDataException("Specialist output cites evidence outside the task evidence boundary.");
        }
    }

    private sealed record ExecutionIdentity(
        Guid ProjectId,
        string RunId,
        string ExecutionId,
        string CapabilityId,
        string InputDigestSha256,
        string AuthorityDigestSha256,
        string IdentityDigestSha256)
    {
        public static ExecutionIdentity Create(
            SpecialistExecutionContext context,
            MissionSpecialistKind kind)
        {
            var authorityParts = new List<string>
            {
                "aevrix-mission-authority-v1",
                context.ProjectId.ToString("D"),
                context.MissionId,
                context.TargetId,
                context.Task.TaskId,
                kind.ToString(),
                context.Task.Required ? "required" : "optional"
            };
            authorityParts.AddRange(context.Task.EvidenceIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "evidence:" + value));
            authorityParts.AddRange(context.Task.DependsOn
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "depends:" + value));
            var authority = Digest(authorityParts);

            var inputParts = new List<string>
            {
                "aevrix-mission-input-v1",
                authority,
                context.Task.Objective
            };
            foreach (var dependency in context.DependencyResults.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                inputParts.Add("dependency:" + dependency.Key);
                inputParts.Add(DigestDependency(dependency.Value));
            }
            var input = Digest(inputParts);

            var executionId = MissionExecutionProofIdentity.CreateExecutionId(
                context.ProjectId,
                context.MissionId,
                context.TargetId,
                context.Task.TaskId,
                kind);
            var identity = executionId["mission-task:".Length..];

            return new ExecutionIdentity(
                context.ProjectId,
                context.MissionId,
                executionId,
                kind.ToString(),
                input,
                authority,
                identity);
        }

        public ExecutionProofEvent CreateStarted(DateTimeOffset observedAt) => new(
            EventId: "proof-start:" + IdentityDigestSha256,
            ProjectId,
            RunId,
            ExecutionId,
            ExecutionProofStage.Started,
            CapabilityClass,
            CapabilityId,
            ExecutionProofOutcome.Pending,
            InputDigestSha256,
            AuthorityDigestSha256,
            ResultDigestSha256: null,
            AttestationDigestSha256: null,
            ArtifactManifestSha256: null,
            ValidationDigestSha256: null,
            JudgeDecisionDigestSha256: null,
            PromotionDigestSha256: null,
            PromotionReference: null,
            observedAt);

        public ExecutionProofEvent CreateSucceeded(
            SpecialistExecutionOutput output,
            DateTimeOffset observedAt) => new(
            EventId: "proof-complete:" + IdentityDigestSha256,
            ProjectId,
            RunId,
            ExecutionId,
            ExecutionProofStage.Completed,
            CapabilityClass,
            CapabilityId,
            ExecutionProofOutcome.Succeeded,
            InputDigestSha256,
            AuthorityDigestSha256,
            ResultDigestSha256: DigestOutput(output),
            AttestationDigestSha256: null,
            ArtifactManifestSha256: output.ArtifactIds.Count == 0
                ? null
                : DigestArtifactManifest(output.ArtifactIds),
            ValidationDigestSha256: null,
            JudgeDecisionDigestSha256: null,
            PromotionDigestSha256: null,
            PromotionReference: null,
            observedAt);

        public ExecutionProofEvent CreateFailed(
            Type exceptionType,
            DateTimeOffset observedAt) => new(
            EventId: "proof-complete:" + IdentityDigestSha256,
            ProjectId,
            RunId,
            ExecutionId,
            ExecutionProofStage.Completed,
            CapabilityClass,
            CapabilityId,
            ExecutionProofOutcome.Failed,
            InputDigestSha256,
            AuthorityDigestSha256,
            ResultDigestSha256: Digest([
                "aevrix-mission-failure-v1",
                exceptionType.FullName ?? exceptionType.Name
            ]),
            AttestationDigestSha256: null,
            ArtifactManifestSha256: null,
            ValidationDigestSha256: null,
            JudgeDecisionDigestSha256: null,
            PromotionDigestSha256: null,
            PromotionReference: null,
            observedAt);

        private static string DigestDependency(SpecialistTaskResult result)
        {
            var parts = new List<string>
            {
                "aevrix-mission-dependency-v1",
                result.TaskId,
                result.Specialist.ToString(),
                result.State.ToString(),
                result.Summary,
                result.Confidence.ToString("R", CultureInfo.InvariantCulture),
                result.ErrorType ?? string.Empty,
                result.CompletedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            };
            parts.AddRange(result.EvidenceIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "evidence:" + value));
            parts.AddRange(result.ArtifactIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "artifact:" + value));
            return Digest(parts);
        }

        private static string DigestOutput(SpecialistExecutionOutput output)
        {
            var parts = new List<string>
            {
                "aevrix-mission-result-v1",
                output.Summary,
                output.Confidence.ToString("R", CultureInfo.InvariantCulture)
            };
            parts.AddRange(output.EvidenceIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "evidence:" + value));
            parts.AddRange(output.ArtifactIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "artifact:" + value));
            return Digest(parts);
        }

        private static string DigestArtifactManifest(IReadOnlyList<string> artifactIds)
        {
            var parts = new List<string> { "aevrix-mission-artifact-manifest-v1" };
            parts.AddRange(artifactIds
                .OrderBy(static value => value, StringComparer.Ordinal)
                .Select(static value => "artifact:" + value));
            return Digest(parts);
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
}
