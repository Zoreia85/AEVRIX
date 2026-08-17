namespace Aevrix.Core;

public enum ArtifactInspectionStep
{
    ComputeSha256,
    DetectFormatByMagic,
    ExtractMetadata,
    IdentifyEncryption,
    EnumerateContainer,
    DecompressReadOnly,
    ExtractReadOnly,
    ParseDocumentStructure,
    ParseStructuredData,
    ParseDatabaseReadOnly,
    StaticDisassembly,
    ParseNetworkCapture,
    ParseBlockchainStructure,
    ConvertToCanonicalIntermediate,
    ScanNestedArtifacts,
    RequireCryptographicAuthorization
}

public sealed record ArtifactQuarantinePolicy(
    long MaxInputBytes,
    long MaxExpandedBytes,
    int MaxExtractedFiles,
    int MaxNestingDepth,
    bool ReadOnly,
    bool NetworkAllowed,
    bool ExecutionAllowed,
    bool PreserveOriginal,
    bool PreserveSha256,
    bool PlaintextPromotionRequiresJudge)
{
    public static ArtifactQuarantinePolicy Default { get; } = new(
        MaxInputBytes: 4L * 1024 * 1024 * 1024,
        MaxExpandedBytes: 16L * 1024 * 1024 * 1024,
        MaxExtractedFiles: 100_000,
        MaxNestingDepth: 12,
        ReadOnly: true,
        NetworkAllowed: false,
        ExecutionAllowed: false,
        PreserveOriginal: true,
        PreserveSha256: true,
        PlaintextPromotionRequiresJudge: true);
}

public sealed record ArtifactInspectionPlan(
    TargetKind Kind,
    ArtifactQuarantinePolicy Policy,
    IReadOnlyList<ArtifactInspectionStep> Steps,
    bool Encrypted,
    bool RequiresCryptographicAuthorization);

/// <summary>
/// Produces a bounded, read-only inspection plan. It never executes the artifact and
/// never defeats cryptography. Encrypted content is fingerprinted and classified, then
/// paused behind the separate cryptographic authorization contract.
/// </summary>
public static class ArtifactInspectionPlanner
{
    public static ArtifactInspectionPlan Create(
        TargetRoute route,
        bool encrypted,
        ArtifactQuarantinePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        if (route.TransportSecurity is not TransportSecurity.LocalArtifact)
        {
            throw new ArgumentException(
                "Artifact inspection planning requires a captured local artifact, not a live endpoint.",
                nameof(route));
        }

        var effectivePolicy = policy ?? ArtifactQuarantinePolicy.Default;
        ValidatePolicy(effectivePolicy);

        var steps = new List<ArtifactInspectionStep>
        {
            ArtifactInspectionStep.ComputeSha256,
            ArtifactInspectionStep.DetectFormatByMagic,
            ArtifactInspectionStep.ExtractMetadata,
            ArtifactInspectionStep.IdentifyEncryption
        };

        switch (route.Kind)
        {
            case TargetKind.ArchiveContainer:
            case TargetKind.JavaArchive:
            case TargetKind.AndroidApk:
            case TargetKind.AndroidAppBundle:
            case TargetKind.AndroidXapk:
            case TargetKind.AppleIpa:
                steps.Add(ArtifactInspectionStep.EnumerateContainer);
                steps.Add(ArtifactInspectionStep.DecompressReadOnly);
                steps.Add(ArtifactInspectionStep.ExtractReadOnly);
                steps.Add(ArtifactInspectionStep.ScanNestedArtifacts);
                break;

            case TargetKind.DiskImage:
            case TargetKind.VirtualMachineImage:
            case TargetKind.MacDiskImage:
            case TargetKind.MacInstallerPackage:
            case TargetKind.WindowsInstaller:
            case TargetKind.LinuxPackage:
                steps.Add(ArtifactInspectionStep.EnumerateContainer);
                steps.Add(ArtifactInspectionStep.ExtractReadOnly);
                steps.Add(ArtifactInspectionStep.ScanNestedArtifacts);
                break;

            case TargetKind.DatabaseArtifact:
                steps.Add(ArtifactInspectionStep.ParseDatabaseReadOnly);
                steps.Add(ArtifactInspectionStep.ConvertToCanonicalIntermediate);
                break;

            case TargetKind.DocumentArtifact:
                steps.Add(ArtifactInspectionStep.ParseDocumentStructure);
                steps.Add(ArtifactInspectionStep.ConvertToCanonicalIntermediate);
                steps.Add(ArtifactInspectionStep.ScanNestedArtifacts);
                break;

            case TargetKind.StructuredDataArtifact:
                steps.Add(ArtifactInspectionStep.ParseStructuredData);
                steps.Add(ArtifactInspectionStep.ConvertToCanonicalIntermediate);
                break;

            case TargetKind.NetworkCapture:
                steps.Add(ArtifactInspectionStep.ParseNetworkCapture);
                steps.Add(ArtifactInspectionStep.ConvertToCanonicalIntermediate);
                break;

            case TargetKind.SmartContractSource:
            case TargetKind.BlockchainArtifact:
                steps.Add(ArtifactInspectionStep.ParseBlockchainStructure);
                steps.Add(ArtifactInspectionStep.ConvertToCanonicalIntermediate);
                break;

            case TargetKind.WebAssemblyModule:
            case TargetKind.NativeOrBytecodeArtifact:
            case TargetKind.WindowsExecutable:
            case TargetKind.WindowsLibrary:
            case TargetKind.LinuxAppImage:
            case TargetKind.FirmwareArtifact:
                steps.Add(ArtifactInspectionStep.StaticDisassembly);
                steps.Add(ArtifactInspectionStep.ConvertToCanonicalIntermediate);
                break;
        }

        if (encrypted)
        {
            steps.Add(ArtifactInspectionStep.RequireCryptographicAuthorization);
        }

        return new ArtifactInspectionPlan(
            route.Kind,
            effectivePolicy,
            steps.Distinct().ToArray(),
            encrypted,
            RequiresCryptographicAuthorization: encrypted);
    }

    private static void ValidatePolicy(ArtifactQuarantinePolicy policy)
    {
        if (policy.MaxInputBytes <= 0 || policy.MaxExpandedBytes <= 0 ||
            policy.MaxExpandedBytes < policy.MaxInputBytes ||
            policy.MaxExtractedFiles <= 0 || policy.MaxNestingDepth <= 0)
        {
            throw new ArgumentException("Artifact quarantine limits must be positive and internally consistent.", nameof(policy));
        }

        if (!policy.ReadOnly || policy.NetworkAllowed || policy.ExecutionAllowed ||
            !policy.PreserveOriginal || !policy.PreserveSha256 || !policy.PlaintextPromotionRequiresJudge)
        {
            throw new ArgumentException(
                "AEVRIX artifact quarantine must remain read-only, offline, non-executing, provenance-preserving and Judge-gated.",
                nameof(policy));
        }
    }
}
