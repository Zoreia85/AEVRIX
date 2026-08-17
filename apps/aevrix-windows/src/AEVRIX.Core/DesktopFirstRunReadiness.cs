namespace Aevrix.Core;

public enum DesktopOperatingMode
{
    LocalSupervised,
    RemoteGoverned
}

public enum DesktopReadinessStatus
{
    Ready,
    Pending,
    Blocked
}

public sealed record DesktopReadinessGate(
    string Id,
    string Title,
    DesktopReadinessStatus Status,
    string Detail);

public sealed record DesktopFirstRunSignals(
    bool StructuralIntegrityAttempted,
    bool StructuralIntegrityVerified,
    bool EngineHostVerificationAttempted,
    bool EngineHostAuthenticated,
    DeviceKeySecurityTier? DeviceSecurityTier,
    bool DeviceCertificateValidated,
    bool RemoteEndpointConfigured,
    bool RemoteSessionAuthenticated,
    DesktopOperatingMode? RequestedMode,
    bool PermissionsAcknowledged);

public sealed record DesktopFirstRunEvaluation(
    IReadOnlyList<DesktopReadinessGate> Gates,
    bool CanComplete,
    string Summary)
{
    public DesktopReadinessGate Gate(string id) =>
        Gates.First(gate => string.Equals(gate.Id, id, StringComparison.Ordinal));
}

public static class DesktopFirstRunReadiness
{
    public static DesktopFirstRunEvaluation Evaluate(DesktopFirstRunSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var gates = new List<DesktopReadinessGate>
        {
            EvaluateStructuralIntegrity(signals),
            EvaluateEngineHost(signals),
            EvaluateDeviceIdentity(signals),
            EvaluateOperatingMode(signals),
            EvaluateRemoteIdentity(signals),
            EvaluatePermissions(signals)
        };

        var canComplete = gates.All(gate => gate.Status == DesktopReadinessStatus.Ready);
        var summary = canComplete
            ? "Pronto para concluir a inicialização deste modo operacional."
            : BuildBlockedSummary(gates);

        return new DesktopFirstRunEvaluation(gates, canComplete, summary);
    }

    private static DesktopReadinessGate EvaluateStructuralIntegrity(DesktopFirstRunSignals signals)
    {
        if (!signals.StructuralIntegrityAttempted)
        {
            return Pending(
                "integrity",
                "Integridade estrutural local",
                "A estrutura de binários obrigatórios ainda não foi verificada nesta execução.");
        }

        return signals.StructuralIntegrityVerified
            ? Ready(
                "integrity",
                "Integridade estrutural local",
                "Binários obrigatórios foram encontrados, lidos e verificados contra redirecionamentos locais. Esta prova não substitui assinatura de release.")
            : Blocked(
                "integrity",
                "Integridade estrutural local",
                "A estrutura local não passou na verificação. A inicialização permanece bloqueada.");
    }

    private static DesktopReadinessGate EvaluateEngineHost(DesktopFirstRunSignals signals)
    {
        if (!signals.EngineHostVerificationAttempted)
        {
            return Pending(
                "enginehost",
                "EngineHost autenticado",
                "Ainda não foi executado um Ping autenticado do processo supervisionado.");
        }

        return signals.EngineHostAuthenticated
            ? Ready(
                "enginehost",
                "EngineHost autenticado",
                "O processo local respondeu ao protocolo autenticado esperado nesta sessão.")
            : Blocked(
                "enginehost",
                "EngineHost autenticado",
                "A prova autenticada do EngineHost não está válida nesta sessão.");
    }

    private static DesktopReadinessGate EvaluateDeviceIdentity(DesktopFirstRunSignals signals)
    {
        return signals.DeviceSecurityTier switch
        {
            DeviceKeySecurityTier.TpmNonExportable => Ready(
                "device-identity",
                "Identidade do dispositivo",
                "Chave ECDSA P-256 não exportável vinculada ao provedor TPM foi preparada."),
            DeviceKeySecurityTier.SoftwareNonExportable => Ready(
                "device-identity",
                "Identidade do dispositivo",
                "Chave ECDSA P-256 não exportável em software foi preparada por decisão explícita."),
            null => Pending(
                "device-identity",
                "Identidade do dispositivo",
                "Nenhuma identidade local não exportável foi comprovada nesta execução."),
            _ => Blocked(
                "device-identity",
                "Identidade do dispositivo",
                "O tier da identidade local não é reconhecido.")
        };
    }

    private static DesktopReadinessGate EvaluateOperatingMode(DesktopFirstRunSignals signals)
    {
        return signals.RequestedMode switch
        {
            DesktopOperatingMode.LocalSupervised => Ready(
                "operating-mode",
                "Modo operacional",
                "Modo local supervisionado selecionado. Nenhuma capacidade remota é inferida."),
            DesktopOperatingMode.RemoteGoverned => Ready(
                "operating-mode",
                "Modo operacional",
                "Modo remoto governado selecionado. A conclusão exige endpoint, certificado e sessão remota válidos."),
            null => Pending(
                "operating-mode",
                "Modo operacional",
                "Selecione explicitamente o modo operacional antes de concluir."),
            _ => Blocked(
                "operating-mode",
                "Modo operacional",
                "O modo operacional solicitado não é reconhecido.")
        };
    }

    private static DesktopReadinessGate EvaluateRemoteIdentity(DesktopFirstRunSignals signals)
    {
        if (signals.RequestedMode != DesktopOperatingMode.RemoteGoverned)
        {
            return Ready(
                "remote-identity",
                "Identidade e sessão remotas",
                "Não exigidas no modo local supervisionado. O estado remoto permanece indisponível.");
        }

        if (!signals.RemoteEndpointConfigured)
        {
            return Blocked(
                "remote-identity",
                "Identidade e sessão remotas",
                "Nenhum endpoint remoto governado foi configurado.");
        }

        if (!signals.DeviceCertificateValidated)
        {
            return Blocked(
                "remote-identity",
                "Identidade e sessão remotas",
                "O certificado cliente emitido para este dispositivo ainda não foi validado.");
        }

        if (!signals.RemoteSessionAuthenticated)
        {
            return Blocked(
                "remote-identity",
                "Identidade e sessão remotas",
                "Nenhuma sessão remota autenticada foi comprovada.");
        }

        return Ready(
            "remote-identity",
            "Identidade e sessão remotas",
            "Endpoint, certificado do dispositivo e sessão remota foram comprovados.");
    }

    private static DesktopReadinessGate EvaluatePermissions(DesktopFirstRunSignals signals)
    {
        return signals.PermissionsAcknowledged
            ? Ready(
                "permissions",
                "Postura de permissões",
                "O usuário confirmou que o Desktop não eleva privilégios nem ignora políticas de runtime automaticamente.")
            : Pending(
                "permissions",
                "Postura de permissões",
                "É necessário reconhecer a postura de privilégios e isolamento antes de concluir.");
    }

    private static string BuildBlockedSummary(IEnumerable<DesktopReadinessGate> gates)
    {
        var blockers = gates
            .Where(gate => gate.Status != DesktopReadinessStatus.Ready)
            .Select(gate => gate.Title)
            .ToArray();

        return blockers.Length == 0
            ? "Inicialização ainda não concluída."
            : $"Pendente: {string.Join(", ", blockers)}.";
    }

    private static DesktopReadinessGate Ready(string id, string title, string detail) =>
        new(id, title, DesktopReadinessStatus.Ready, detail);

    private static DesktopReadinessGate Pending(string id, string title, string detail) =>
        new(id, title, DesktopReadinessStatus.Pending, detail);

    private static DesktopReadinessGate Blocked(string id, string title, string detail) =>
        new(id, title, DesktopReadinessStatus.Blocked, detail);
}
