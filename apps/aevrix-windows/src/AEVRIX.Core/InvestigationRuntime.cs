using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aevrix.Core;

public sealed record InvestigationRuntimeArtifactFingerprint(
    string DisplayName,
    long SizeBytes,
    string Sha256);

public sealed record InvestigationRuntimeRegistration(
    Guid InvestigationId,
    string Workspace,
    InvestigationTargetKind TargetKind,
    InvestigationStrategy Strategy,
    string AuthorizationClass,
    InvestigationPriority Priority,
    IReadOnlyList<InvestigationInputArtifact> Artifacts)
{
    public void Validate()
    {
        if (InvestigationId == Guid.Empty)
        {
            throw new ArgumentException("Investigation id must not be empty.", nameof(InvestigationId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(Workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuthorizationClass);
        ArgumentNullException.ThrowIfNull(Artifacts);

        if (AuthorizationClass is not ("owned" or "authorized" or "clean-room"))
        {
            throw new ArgumentException("Authorization class is not recognized.", nameof(AuthorizationClass));
        }
        if (InvestigationDraft.RequiresExecutableArtifacts(TargetKind) && Artifacts.Count == 0)
        {
            throw new ArgumentException(
                "Executable application targets require at least one local artifact.",
                nameof(Artifacts));
        }
    }
}

public sealed record InvestigationRuntimeRecord(
    string MissionId,
    Guid InvestigationId,
    string Workspace,
    InvestigationTargetKind TargetKind,
    InvestigationStrategy Strategy,
    string AuthorizationClass,
    InvestigationPriority Priority,
    InvestigationRunState State,
    InvestigationPhase CurrentPhase,
    int QueuePosition,
    InvestigationResourceBudget Budget,
    double PercentComplete,
    TimeSpan? EstimatedRemaining,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityAtUtc,
    string? Blocker,
    IReadOnlyList<InvestigationRuntimeArtifactFingerprint> Artifacts,
    IReadOnlyList<InvestigationStageProgress> Stages,
    IReadOnlyList<InvestigationProgressEvidenceSample> ProgressEvidence)
{
    public InvestigationProgressSnapshot ToProgressSnapshot()
        => InvestigationProgressSnapshot.Create(
            State,
            CurrentPhase,
            Stages,
            CreatedAtUtc,
            LastActivityAtUtc,
            Blocker,
            ProgressEvidence);
}

public sealed class InvestigationRuntimeCoordinator
{
    private const string StoreFileName = "investigation-runtime.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InvestigationRuntimeCoordinator(AevrixDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var ensured = paths.EnsureCreated();
        _storePath = Path.Combine(ensured.EngineRoot, StoreFileName);
    }

    public async Task<InvestigationRuntimeRecord> RegisterAsync(
        InvestigationRuntimeRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Validate();

        var fingerprints = await FingerprintArtifactsAsync(registration.Artifacts, cancellationToken)
            .ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var stages = CreateDefaultStages(registration.Strategy);
        var intakeIndex = Array.FindIndex(stages, stage => stage.Phase == InvestigationPhase.IntakeAndAuthorization);
        if (intakeIndex >= 0)
        {
            stages[intakeIndex] = stages[intakeIndex] with { Completion = 1 };
        }

        var progress = ComputePercent(stages);
        var intakeEvidence = CreateProgressEvidence(
            registration.InvestigationId,
            now,
            progress,
            "intake");
        var capacity = LocalCapacityRecommendation.ForCurrentProcess();
        var budget = InvestigationResourceBudget.ConservativeDefault(capacity);
        var record = new InvestigationRuntimeRecord(
            MissionId: "MIS-" + registration.InvestigationId.ToString("N").ToUpperInvariant(),
            InvestigationId: registration.InvestigationId,
            Workspace: registration.Workspace.Trim(),
            TargetKind: registration.TargetKind,
            Strategy: registration.Strategy,
            AuthorizationClass: registration.AuthorizationClass,
            Priority: registration.Priority,
            State: InvestigationRunState.Ready,
            CurrentPhase: InvestigationPhase.Acquisition,
            QueuePosition: 0,
            Budget: budget,
            PercentComplete: progress,
            EstimatedRemaining: null,
            CreatedAtUtc: now,
            LastActivityAtUtc: now,
            Blocker: null,
            Artifacts: fingerprints,
            Stages: stages,
            ProgressEvidence: [intakeEvidence]);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var existingIndex = records.FindIndex(item => item.InvestigationId == registration.InvestigationId);
            if (existingIndex >= 0)
            {
                return records[existingIndex];
            }
            records.Add(record);
            await SaveUnsafeAsync(records, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InvestigationRuntimeRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return records
                .OrderByDescending(item => item.LastActivityAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<InvestigationRuntimeRecord>> ReconcileScheduleAsync(
        LocalCapacityRecommendation? capacity = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (records.Count == 0)
            {
                return Array.Empty<InvestigationRuntimeRecord>();
            }

            var effectiveCapacity = capacity ?? LocalCapacityRecommendation.ForCurrentProcess();
            var now = DateTimeOffset.UtcNow;
            var requests = records.Select(item => new InvestigationScheduleRequest(
                item.InvestigationId,
                item.Priority,
                item.CreatedAtUtc,
                item.State));
            var decisions = InvestigationScheduler.Plan(requests, effectiveCapacity, now)
                .ToDictionary(item => item.InvestigationId);

            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (!decisions.TryGetValue(record.InvestigationId, out var decision))
                {
                    continue;
                }

                var next = record;
                if (record.State != decision.NextState)
                {
                    InvestigationStateMachine.RequireTransition(record.State, decision.NextState);
                    next = next with
                    {
                        State = decision.NextState,
                        LastActivityAtUtc = now
                    };
                }

                next = next with
                {
                    QueuePosition = decision.QueuePosition,
                    Budget = decision.Budget
                };

                // This public/runtime increment deliberately performs only the promoted intake
                // and admission infrastructure. It must not pretend acquisition/static/dynamic
                // analysis happened when target adapters are not yet connected.
                if (next.State == InvestigationRunState.Running)
                {
                    InvestigationStateMachine.RequireTransition(
                        InvestigationRunState.Running,
                        InvestigationRunState.Blocked);
                    next = next with
                    {
                        State = InvestigationRunState.Blocked,
                        CurrentPhase = InvestigationPhase.Acquisition,
                        QueuePosition = 0,
                        LastActivityAtUtc = now,
                        Blocker = "A admissão local foi concluída; a aquisição real aguarda um adapter promovido e autorizado para este tipo de alvo."
                    };
                }

                next = RefreshProgress(next);
                records[index] = next;
            }

            await SaveUnsafeAsync(records, cancellationToken).ConfigureAwait(false);
            return records.OrderByDescending(item => item.LastActivityAtUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InvestigationRuntimeRecord> PauseAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(
            investigationId,
            InvestigationRunState.Paused,
            "Pausada por solicitação explícita do usuário.",
            cancellationToken).ConfigureAwait(false);

    public async Task<InvestigationRuntimeRecord> ResumeAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(
            investigationId,
            InvestigationRunState.Queued,
            null,
            cancellationToken).ConfigureAwait(false);

    public async Task<InvestigationRuntimeRecord> CancelAsync(
        Guid investigationId,
        CancellationToken cancellationToken = default)
        => await TransitionAsync(
            investigationId,
            InvestigationRunState.Cancelled,
            "Cancelada por solicitação explícita do usuário.",
            cancellationToken).ConfigureAwait(false);

    public async Task<InvestigationRuntimeRecord> RecordVerifiedProgressAsync(
        Guid investigationId,
        InvestigationPhase currentPhase,
        IReadOnlyList<InvestigationStageProgress> stages,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stages);
        WorkspaceScope.ValidateToken(evidenceId, nameof(evidenceId));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(item => item.InvestigationId == investigationId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Investigation runtime record was not found.");
            }

            var current = records[index];
            var now = DateTimeOffset.UtcNow;
            var percent = ComputePercent(stages);
            if (percent + 0.0001 < current.PercentComplete)
            {
                throw new InvalidOperationException("Verified progress cannot move backwards.");
            }
            if (current.ProgressEvidence.Any(sample => string.Equals(sample.EvidenceId, evidenceId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Progress evidence id must be unique within the investigation.");
            }

            var evidence = new InvestigationProgressEvidenceSample(now, percent, evidenceId);
            var history = current.ProgressEvidence.Append(evidence).ToArray();
            var updated = current with
            {
                CurrentPhase = currentPhase,
                Stages = stages.Select(stage => stage.Normalize()).ToArray(),
                ProgressEvidence = history,
                LastActivityAtUtc = now,
                Blocker = null
            };
            updated = RefreshProgress(updated);
            records[index] = updated;
            await SaveUnsafeAsync(records, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InvestigationRuntimeRecord> TransitionAsync(
        Guid investigationId,
        InvestigationRunState nextState,
        string? blocker,
        CancellationToken cancellationToken)
    {
        if (investigationId == Guid.Empty)
        {
            throw new ArgumentException("Investigation id must not be empty.", nameof(investigationId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(item => item.InvestigationId == investigationId);
            if (index < 0)
            {
                throw new KeyNotFoundException("Investigation runtime record was not found.");
            }

            var current = records[index];
            InvestigationStateMachine.RequireTransition(current.State, nextState);
            var updated = current with
            {
                State = nextState,
                QueuePosition = 0,
                LastActivityAtUtc = DateTimeOffset.UtcNow,
                Blocker = blocker
            };
            updated = RefreshProgress(updated);
            records[index] = updated;
            await SaveUnsafeAsync(records, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static InvestigationRuntimeRecord RefreshProgress(InvestigationRuntimeRecord record)
    {
        var snapshot = record.ToProgressSnapshot();
        return record with
        {
            PercentComplete = snapshot.PercentComplete,
            EstimatedRemaining = snapshot.EstimatedRemaining
        };
    }

    private async Task<List<InvestigationRuntimeRecord>> LoadUnsafeAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return [];
        }

        await using var stream = new FileStream(
            _storePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            return await JsonSerializer.DeserializeAsync<List<InvestigationRuntimeRecord>>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Investigation runtime store is invalid; runtime remains fail-closed.", ex);
        }
    }

    private async Task SaveUnsafeAsync(
        IReadOnlyList<InvestigationRuntimeRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storePath)
            ?? throw new InvalidOperationException("Investigation runtime store directory is invalid.");
        Directory.CreateDirectory(directory);
        var tempPath = _storePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    records,
                    JsonOptions,
                    cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static InvestigationStageProgress[] CreateDefaultStages(InvestigationStrategy strategy)
    {
        var stages = new List<InvestigationStageProgress>
        {
            new(InvestigationPhase.IntakeAndAuthorization, 10, 0),
            new(InvestigationPhase.Acquisition, 15, 0),
            new(InvestigationPhase.StaticAnalysis, 20, 0)
        };

        if (strategy is InvestigationStrategy.InvestigateAndEmulate or InvestigationStrategy.InvestigateAndBuildParallel)
        {
            stages.Add(new InvestigationStageProgress(InvestigationPhase.DynamicObservation, 15, 0));
        }
        stages.Add(new InvestigationStageProgress(InvestigationPhase.EvidenceCorrelation, 15, 0));
        stages.Add(new InvestigationStageProgress(InvestigationPhase.BlueprintSynthesis, 15, 0));

        if (strategy is InvestigationStrategy.InvestigateAndBuildParallel or InvestigationStrategy.ReconstructWhiteLabel)
        {
            stages.Add(new InvestigationStageProgress(InvestigationPhase.Reconstruction, 20, 0));
            stages.Add(new InvestigationStageProgress(InvestigationPhase.DifferentialValidation, 10, 0));
        }
        stages.Add(new InvestigationStageProgress(InvestigationPhase.FinalQualityAssurance, 10, 0));
        return stages.ToArray();
    }

    private static double ComputePercent(IEnumerable<InvestigationStageProgress> stages)
    {
        var normalized = stages.Select(stage => stage.Normalize()).ToArray();
        var total = normalized.Sum(stage => stage.Weight);
        if (total <= 0)
        {
            return 0;
        }
        return Math.Round(
            Math.Clamp(normalized.Sum(stage => stage.Weight * stage.Completion) / total * 100, 0, 100),
            1);
    }

    private static InvestigationProgressEvidenceSample CreateProgressEvidence(
        Guid investigationId,
        DateTimeOffset sampledAtUtc,
        double percent,
        string eventKind)
    {
        var canonical = string.Join('|', new[]
        {
            "AEVRIX-RUNTIME-PROGRESS-V1",
            investigationId.ToString("N"),
            sampledAtUtc.ToUniversalTime().ToString("O"),
            percent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
            eventKind
        });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new InvestigationProgressEvidenceSample(
            sampledAtUtc,
            percent,
            "RUNTIME-" + digest[..24]);
    }

    private static async Task<IReadOnlyList<InvestigationRuntimeArtifactFingerprint>> FingerprintArtifactsAsync(
        IReadOnlyList<InvestigationInputArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        if (artifacts.Count == 0)
        {
            return Array.Empty<InvestigationRuntimeArtifactFingerprint>();
        }

        var result = new List<InvestigationRuntimeArtifactFingerprint>(artifacts.Count);
        foreach (var artifact in artifacts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.DisplayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Path);
            var fullPath = Path.GetFullPath(artifact.Path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Investigation input artifact was not found.", fullPath);
            }

            var info = new FileInfo(fullPath);
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            result.Add(new InvestigationRuntimeArtifactFingerprint(
                Path.GetFileName(artifact.DisplayName),
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }
        return result;
    }
}
