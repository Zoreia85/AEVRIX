using System.Text.Json.Serialization;

namespace Aevrix.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EnginePingCommand), "ping")]
[JsonDerivedType(typeof(StartCaptureCommand), "startCapture")]
[JsonDerivedType(typeof(ResumeCaptureCommand), "resumeCapture")]
[JsonDerivedType(typeof(StopCaptureCommand), "stopCapture")]
[JsonDerivedType(typeof(DiagnoseEngineCommand), "diagnoseEngine")]
[JsonDerivedType(typeof(GetEngineStatusCommand), "getEngineStatus")]
[JsonDerivedType(typeof(ResearchBrowserSelfTestCommand), "researchBrowserSelfTest")]
[JsonDerivedType(typeof(GenerateBlueprintCommand), "generateBlueprint")]
[JsonDerivedType(typeof(RegisterInvestigationRuntimeCommand), "registerInvestigationRuntime")]
[JsonDerivedType(typeof(ListInvestigationRuntimeCommand), "listInvestigationRuntime")]
[JsonDerivedType(typeof(ReconcileInvestigationScheduleCommand), "reconcileInvestigationSchedule")]
[JsonDerivedType(typeof(PauseInvestigationRuntimeCommand), "pauseInvestigationRuntime")]
[JsonDerivedType(typeof(ResumeInvestigationRuntimeCommand), "resumeInvestigationRuntime")]
[JsonDerivedType(typeof(CancelInvestigationRuntimeCommand), "cancelInvestigationRuntime")]
public abstract record EngineCommand(string RequestId, int ProtocolVersion = EngineProtocol.CurrentVersion);

public sealed record EnginePingCommand(string RequestId) : EngineCommand(RequestId);

public sealed record StartCaptureCommand(
    string RequestId,
    string TargetId,
    string ProjectId,
    string CaptureId,
    ProjectDomain Domain,
    Uri? EntryPoint,
    string? ArtifactPath) : EngineCommand(RequestId);

public sealed record ResumeCaptureCommand(
    string RequestId,
    string TargetId,
    string CaptureId) : EngineCommand(RequestId);

public sealed record StopCaptureCommand(
    string RequestId,
    string CaptureId) : EngineCommand(RequestId);

public sealed record DiagnoseEngineCommand(string RequestId, bool Repair) : EngineCommand(RequestId);

public sealed record GetEngineStatusCommand(string RequestId) : EngineCommand(RequestId);

public sealed record ResearchBrowserSelfTestCommand(string RequestId) : EngineCommand(RequestId);

public sealed record GenerateBlueprintCommand(
    string RequestId,
    string ProjectId,
    string CaptureId,
    bool IncludeRawEvidenceReferences = false) : EngineCommand(RequestId);

public sealed record RegisterInvestigationRuntimeCommand(
    string RequestId,
    Guid InvestigationId,
    string Workspace,
    InvestigationTargetKind TargetKind,
    InvestigationStrategy Strategy,
    string AuthorizationClass,
    InvestigationPriority Priority,
    IReadOnlyList<InvestigationInputArtifact> Artifacts) : EngineCommand(RequestId);

public sealed record ListInvestigationRuntimeCommand(string RequestId) : EngineCommand(RequestId);

public sealed record ReconcileInvestigationScheduleCommand(string RequestId) : EngineCommand(RequestId);

public sealed record PauseInvestigationRuntimeCommand(
    string RequestId,
    Guid InvestigationId) : EngineCommand(RequestId);

public sealed record ResumeInvestigationRuntimeCommand(
    string RequestId,
    Guid InvestigationId) : EngineCommand(RequestId);

public sealed record CancelInvestigationRuntimeCommand(
    string RequestId,
    Guid InvestigationId) : EngineCommand(RequestId);

public sealed record EngineResponse(
    string RequestId,
    bool Success,
    string Code,
    string Message,
    object? Data = null,
    int ProtocolVersion = EngineProtocol.CurrentVersion);

public static class EngineProtocol
{
    public const int CurrentVersion = 4;
    public const int MaxMessageBytes = 1_048_576;
    public const string PipeNamePrefix = "AEVRIX.Engine.";
    public const string TokenEnvironmentVariable = "AEVRIX_ENGINE_TOKEN";
    public const string PipeEnvironmentVariable = "AEVRIX_ENGINE_PIPE";
    public const string ParentProcessIdEnvironmentVariable = "AEVRIX_ENGINE_PARENT_PID";

    public static string NewCaptureId(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var safeTarget = new string(targetId.Trim().ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' ? ch : '-')
            .ToArray()).Trim('-');
        if (safeTarget.Length < 2)
        {
            safeTarget = "target";
        }
        return $"{safeTarget}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
    }

    public static bool IsSafeCaptureId(string captureId)
    {
        if (string.IsNullOrWhiteSpace(captureId) || captureId.Length is < 3 or > 128)
        {
            return false;
        }

        return !captureId.Contains("..", StringComparison.Ordinal)
            && captureId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }
}
