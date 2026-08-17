namespace Aevrix.Core;

public enum SpecialistLab
{
    WebOnline,
    DesktopOffline,
    Mobile
}

public enum TargetKind
{
    Unknown = 0,
    HttpWebApplication,
    HttpsWebApplication,
    WebSocketEndpoint,
    SecureWebSocketEndpoint,
    GrpcEndpoint,
    SecureGrpcEndpoint,
    FileTransferEndpoint,
    MessageBrokerEndpoint,
    ContentAddressedResource,
    BlockchainRpcEndpoint,
    WindowsExecutable,
    WindowsInstaller,
    WindowsLibrary,
    JavaArchive,
    MacDiskImage,
    MacInstallerPackage,
    LinuxAppImage,
    LinuxPackage,
    ArchiveContainer,
    DiskImage,
    VirtualMachineImage,
    NativeOrBytecodeArtifact,
    WebAssemblyModule,
    DatabaseArtifact,
    DocumentArtifact,
    StructuredDataArtifact,
    NetworkCapture,
    SmartContractSource,
    BlockchainArtifact,
    FirmwareArtifact,
    AndroidApk,
    AndroidAppBundle,
    AndroidXapk,
    AppleIpa
}

public enum RoutingEvidenceStrength
{
    Unknown = 0,
    ExtensionHint,
    TransportVerified,
    ExplicitProtocolDeclaration
}

public enum TransportSecurity
{
    Unknown = 0,
    LocalArtifact,
    Cleartext,
    EncryptedTransport,
    ContentAddressed
}

public enum DelegatedLabAuthority
{
    CandidateEvidenceOnly
}

public sealed record TargetRoute(
    SpecialistLab? Lab,
    TargetKind Kind,
    RoutingEvidenceStrength EvidenceStrength,
    TransportSecurity TransportSecurity,
    string NormalizedTarget,
    bool RequiresContentVerification,
    string Reason)
{
    public bool IsRoutable => Lab is not null && Kind is not TargetKind.Unknown;

    public bool RequiresTransportRiskAcknowledgement =>
        TransportSecurity is TransportSecurity.Cleartext;
}

/// <summary>
/// Performs fail-closed preflight routing into the three specialist AEVRIX labs.
/// Routing is not execution authorization. URI schemes and artifact extensions are hints
/// only; they never prove application identity, file format, safety, provenance, licence,
/// or execution eligibility. The receiving lab must verify content before use.
/// </summary>
public static class TargetIntakeRouter
{
    private static readonly Dictionary<string, (TargetKind Kind, SpecialistLab Lab)> ArtifactRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".exe"] = (TargetKind.WindowsExecutable, SpecialistLab.DesktopOffline),
            [".msi"] = (TargetKind.WindowsInstaller, SpecialistLab.DesktopOffline),
            [".msix"] = (TargetKind.WindowsInstaller, SpecialistLab.DesktopOffline),
            [".appx"] = (TargetKind.WindowsInstaller, SpecialistLab.DesktopOffline),
            [".dll"] = (TargetKind.WindowsLibrary, SpecialistLab.DesktopOffline),
            [".jar"] = (TargetKind.JavaArchive, SpecialistLab.DesktopOffline),
            [".dmg"] = (TargetKind.MacDiskImage, SpecialistLab.DesktopOffline),
            [".pkg"] = (TargetKind.MacInstallerPackage, SpecialistLab.DesktopOffline),
            [".appimage"] = (TargetKind.LinuxAppImage, SpecialistLab.DesktopOffline),
            [".deb"] = (TargetKind.LinuxPackage, SpecialistLab.DesktopOffline),
            [".rpm"] = (TargetKind.LinuxPackage, SpecialistLab.DesktopOffline),

            [".zip"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".7z"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".rar"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".tar"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".tgz"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".gz"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".bz2"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".xz"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),
            [".zst"] = (TargetKind.ArchiveContainer, SpecialistLab.DesktopOffline),

            [".iso"] = (TargetKind.DiskImage, SpecialistLab.DesktopOffline),
            [".img"] = (TargetKind.DiskImage, SpecialistLab.DesktopOffline),
            [".vhd"] = (TargetKind.VirtualMachineImage, SpecialistLab.DesktopOffline),
            [".vhdx"] = (TargetKind.VirtualMachineImage, SpecialistLab.DesktopOffline),
            [".qcow"] = (TargetKind.VirtualMachineImage, SpecialistLab.DesktopOffline),
            [".qcow2"] = (TargetKind.VirtualMachineImage, SpecialistLab.DesktopOffline),
            [".ova"] = (TargetKind.VirtualMachineImage, SpecialistLab.DesktopOffline),
            [".ovf"] = (TargetKind.VirtualMachineImage, SpecialistLab.DesktopOffline),

            [".elf"] = (TargetKind.NativeOrBytecodeArtifact, SpecialistLab.DesktopOffline),
            [".so"] = (TargetKind.NativeOrBytecodeArtifact, SpecialistLab.DesktopOffline),
            [".dylib"] = (TargetKind.NativeOrBytecodeArtifact, SpecialistLab.DesktopOffline),
            [".class"] = (TargetKind.NativeOrBytecodeArtifact, SpecialistLab.DesktopOffline),
            [".dex"] = (TargetKind.NativeOrBytecodeArtifact, SpecialistLab.DesktopOffline),
            [".wasm"] = (TargetKind.WebAssemblyModule, SpecialistLab.DesktopOffline),

            [".sqlite"] = (TargetKind.DatabaseArtifact, SpecialistLab.DesktopOffline),
            [".sqlite3"] = (TargetKind.DatabaseArtifact, SpecialistLab.DesktopOffline),
            [".db"] = (TargetKind.DatabaseArtifact, SpecialistLab.DesktopOffline),
            [".mdb"] = (TargetKind.DatabaseArtifact, SpecialistLab.DesktopOffline),
            [".accdb"] = (TargetKind.DatabaseArtifact, SpecialistLab.DesktopOffline),

            [".pdf"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".doc"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".docx"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".rtf"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".odt"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".xls"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".xlsx"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".ods"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".ppt"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".pptx"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),
            [".odp"] = (TargetKind.DocumentArtifact, SpecialistLab.DesktopOffline),

            [".csv"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".tsv"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".json"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".xml"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".yaml"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".yml"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".toml"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".ini"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),
            [".proto"] = (TargetKind.StructuredDataArtifact, SpecialistLab.DesktopOffline),

            [".pcap"] = (TargetKind.NetworkCapture, SpecialistLab.DesktopOffline),
            [".pcapng"] = (TargetKind.NetworkCapture, SpecialistLab.DesktopOffline),

            [".sol"] = (TargetKind.SmartContractSource, SpecialistLab.DesktopOffline),
            [".vy"] = (TargetKind.SmartContractSource, SpecialistLab.DesktopOffline),
            [".move"] = (TargetKind.SmartContractSource, SpecialistLab.DesktopOffline),
            [".teal"] = (TargetKind.SmartContractSource, SpecialistLab.DesktopOffline),
            [".tact"] = (TargetKind.SmartContractSource, SpecialistLab.DesktopOffline),
            [".abi"] = (TargetKind.BlockchainArtifact, SpecialistLab.DesktopOffline),
            [".rlp"] = (TargetKind.BlockchainArtifact, SpecialistLab.DesktopOffline),
            [".boc"] = (TargetKind.BlockchainArtifact, SpecialistLab.DesktopOffline),

            [".hex"] = (TargetKind.FirmwareArtifact, SpecialistLab.DesktopOffline),
            [".srec"] = (TargetKind.FirmwareArtifact, SpecialistLab.DesktopOffline),
            [".uf2"] = (TargetKind.FirmwareArtifact, SpecialistLab.DesktopOffline),

            [".apk"] = (TargetKind.AndroidApk, SpecialistLab.Mobile),
            [".aab"] = (TargetKind.AndroidAppBundle, SpecialistLab.Mobile),
            [".xapk"] = (TargetKind.AndroidXapk, SpecialistLab.Mobile),
            [".ipa"] = (TargetKind.AppleIpa, SpecialistLab.Mobile)
        };

    private static readonly Dictionary<string, (TargetKind Kind, TransportSecurity Security)> OnlineRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["http"] = (TargetKind.HttpWebApplication, TransportSecurity.Cleartext),
            ["https"] = (TargetKind.HttpsWebApplication, TransportSecurity.EncryptedTransport),
            ["ws"] = (TargetKind.WebSocketEndpoint, TransportSecurity.Cleartext),
            ["wss"] = (TargetKind.SecureWebSocketEndpoint, TransportSecurity.EncryptedTransport),
            ["grpc"] = (TargetKind.GrpcEndpoint, TransportSecurity.Cleartext),
            ["grpcs"] = (TargetKind.SecureGrpcEndpoint, TransportSecurity.EncryptedTransport),
            ["ftp"] = (TargetKind.FileTransferEndpoint, TransportSecurity.Cleartext),
            ["ftps"] = (TargetKind.FileTransferEndpoint, TransportSecurity.EncryptedTransport),
            ["sftp"] = (TargetKind.FileTransferEndpoint, TransportSecurity.EncryptedTransport),
            ["mqtt"] = (TargetKind.MessageBrokerEndpoint, TransportSecurity.Cleartext),
            ["mqtts"] = (TargetKind.MessageBrokerEndpoint, TransportSecurity.EncryptedTransport),
            ["amqp"] = (TargetKind.MessageBrokerEndpoint, TransportSecurity.Cleartext),
            ["amqps"] = (TargetKind.MessageBrokerEndpoint, TransportSecurity.EncryptedTransport),
            ["ipfs"] = (TargetKind.ContentAddressedResource, TransportSecurity.ContentAddressed),
            ["ipns"] = (TargetKind.ContentAddressedResource, TransportSecurity.ContentAddressed)
        };

    public static TargetRoute ClassifyWeb(Uri entryPoint) => ClassifyOnline(entryPoint);

    public static TargetRoute ClassifyOnline(Uri entryPoint)
    {
        ValidateOnlineUri(entryPoint);

        if (!OnlineRoutes.TryGetValue(entryPoint.Scheme, out var route))
        {
            throw new ArgumentException(
                $"Unsupported online scheme '{entryPoint.Scheme}'. AEVRIX must classify it before any connection is attempted.",
                nameof(entryPoint));
        }

        return new TargetRoute(
            SpecialistLab.WebOnline,
            route.Kind,
            RoutingEvidenceStrength.TransportVerified,
            route.Security,
            entryPoint.AbsoluteUri,
            RequiresContentVerification: true,
            route.Security is TransportSecurity.Cleartext
                ? $"The {entryPoint.Scheme} scheme routes to Web/Online but uses cleartext transport. Connection requires an explicit transport-risk gate and must not carry credentials or sensitive payloads."
                : $"The {entryPoint.Scheme} scheme routes to Web/Online. Endpoint identity, authorization and observed behaviour still require evidence.");
    }

    public static TargetRoute ClassifyBlockchainEndpoint(Uri endpoint, string network)
    {
        ValidateOnlineUri(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);

        if (!OnlineRoutes.TryGetValue(endpoint.Scheme, out var transport) ||
            transport.Kind is not (TargetKind.HttpWebApplication or TargetKind.HttpsWebApplication or TargetKind.WebSocketEndpoint or TargetKind.SecureWebSocketEndpoint))
        {
            throw new ArgumentException(
                "Blockchain RPC endpoints must use HTTP(S) or WebSocket(S) transport.",
                nameof(endpoint));
        }

        return new TargetRoute(
            SpecialistLab.WebOnline,
            TargetKind.BlockchainRpcEndpoint,
            RoutingEvidenceStrength.ExplicitProtocolDeclaration,
            transport.Security,
            endpoint.AbsoluteUri,
            RequiresContentVerification: true,
            $"Declared blockchain RPC endpoint for network '{network.Trim()}'. Chain identity, method surface and authorization must be verified before collection. Routing never authorizes transaction signing or broadcast.");
    }

    public static TargetRoute ClassifyArtifact(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        var normalizedPath = Path.GetFullPath(artifactPath);
        var extension = Path.GetExtension(normalizedPath);

        if (string.IsNullOrWhiteSpace(extension) || !ArtifactRoutes.TryGetValue(extension, out var route))
        {
            return new TargetRoute(
                Lab: null,
                TargetKind.Unknown,
                RoutingEvidenceStrength.Unknown,
                TransportSecurity.LocalArtifact,
                normalizedPath,
                RequiresContentVerification: true,
                "Artifact type is not safely routable from its filename. No specialist may execute or transform it until content classification is resolved.");
        }

        return new TargetRoute(
            route.Lab,
            route.Kind,
            RoutingEvidenceStrength.ExtensionHint,
            TransportSecurity.LocalArtifact,
            normalizedPath,
            RequiresContentVerification: true,
            $"The {extension.ToLowerInvariant()} suffix is only a routing hint. The receiving lab must verify magic/structure before parsing, conversion, extraction or execution.");
    }

    private static void ValidateOnlineUri(Uri entryPoint)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);

        if (!entryPoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(entryPoint.Scheme))
        {
            throw new ArgumentException("Online AEVRIX targets require an absolute URI.", nameof(entryPoint));
        }

        if (string.IsNullOrWhiteSpace(entryPoint.Host) &&
            entryPoint.Scheme is not ("ipfs" or "ipns"))
        {
            throw new ArgumentException("Online AEVRIX targets require a host.", nameof(entryPoint));
        }

        if (!string.IsNullOrEmpty(entryPoint.UserInfo))
        {
            throw new ArgumentException("Online AEVRIX targets must not embed credentials in the URI.", nameof(entryPoint));
        }
    }
}

/// <summary>
/// Immutable request for a specialist lab to assist another lab without taking ownership
/// of the project or gaining authority to promote Trusted knowledge or canonical blueprints.
/// Delegated work can return candidate evidence only; Evidence Fusion/Judge remain central.
/// </summary>
public sealed record CrossLabHandoffRequest(
    Guid ProjectId,
    string TargetId,
    SpecialistLab OwningLab,
    SpecialistLab DelegatedLab,
    string WorkPackage,
    IReadOnlyList<string> EvidenceIds,
    DelegatedLabAuthority Authority)
{
    public static CrossLabHandoffRequest Create(
        Guid projectId,
        string targetId,
        SpecialistLab owningLab,
        SpecialistLab delegatedLab,
        string workPackage,
        IEnumerable<string>? evidenceIds = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A cross-lab handoff requires a project id.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workPackage);

        if (owningLab == delegatedLab)
        {
            throw new ArgumentException("A cross-lab handoff must delegate to a different specialist lab.", nameof(delegatedLab));
        }

        var normalizedEvidenceIds = (evidenceIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        return new CrossLabHandoffRequest(
            projectId,
            targetId.Trim().ToLowerInvariant(),
            owningLab,
            delegatedLab,
            workPackage.Trim(),
            normalizedEvidenceIds,
            DelegatedLabAuthority.CandidateEvidenceOnly);
    }
}
