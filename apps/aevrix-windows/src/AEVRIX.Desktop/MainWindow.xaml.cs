using System;
using System.IO;
using Aevrix.Core;
using Aevrix.EngineHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _engineHealthTimer;
    private readonly OperationalActivityJournal _activityJournal = new(capacity: 200);
    private readonly DesktopFirstRunProfileStore _firstRunProfileStore;
    private DesktopFirstRunProfile _firstRunProfile;
    private Exception? _firstRunProfileError;
    private DesktopLocalIntegrityResult? _localIntegrityResult;
    private DeviceKeySecurityTier? _deviceSecurityTier;
    private EngineHostSupervisor? _engineSupervisor;
    private bool _integrityAttempted;
    private bool _engineVerificationAttempted;
    private bool _deviceCertificateValidated;
    private bool _remoteSessionAuthenticated;
    private bool _suppressFirstRunPersistence;
    private bool _engineAuthenticated;
    private bool _engineOperationInProgress;
    private bool _engineHealthProbeInProgress;
    private bool _engineStoppedByUser;
    private bool _isClosing;
    private DateTimeOffset _lastAuthenticatedProbeUtc = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        Title = "AEVRIX Desktop";
        Closed += MainWindow_Closed;

        _firstRunProfileStore = DesktopFirstRunProfileStore.ForCurrentUser();
        try
        {
            _firstRunProfile = _firstRunProfileStore.LoadOrCreate();
        }
        catch (Exception ex)
        {
            _firstRunProfileError = ex;
            _firstRunProfile = DesktopFirstRunProfile.CreateNew();
        }

        InitializeFirstRunControls();
        TryValidatePersistedDeviceCertificate();

        _engineHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _engineHealthTimer.Tick += EngineHealthTimer_Tick;
        _engineHealthTimer.Start();

        SetEngineStatus(
            "Não iniciado",
            "Nenhuma sessão local foi autenticada nesta execução.");
        RecordActivity(
            OperationalActivityLevel.Informational,
            "Desktop",
            "Sessão iniciada",
            "Shell Windows carregado. Estados sem prova permanecem indisponíveis.");

        RefreshFirstRunView();
        var firstRunRequired = _firstRunProfileError is not null || _firstRunProfile.CompletedAtUtc is null;
        RootNavigation.SelectedItem = firstRunRequired ? OnboardingNavItem : HomeNavItem;
        ShowSection(firstRunRequired ? "onboarding" : "home", firstRunRequired ? "Inicialização segura" : "Command Center");
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            ShowSection("home", "Command Center");
            return;
        }

        var route = item.Tag?.ToString() ?? "home";
        var title = item.Content?.ToString() ?? "AEVRIX";
        ShowSection(route, title);
    }

    private void StartAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = NewInvestigationNavItem;
        ShowSection("new", "Nova investigação");
    }

    private void OpenOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = OnboardingNavItem;
        ShowSection("onboarding", "Inicialização segura");
    }

    private void OpenMissionControlButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = MissionControlNavItem;
        ShowSection("mission", "Mission Control");
    }

    private void OpenActivityButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = ActivityNavItem;
        ShowSection("activity", "Activity / Proof Ledger");
    }

    private void RefreshActivityButton_Click(object sender, RoutedEventArgs e)
        => RefreshActivityView();

    private async void VerifyEngineHostButton_Click(object sender, RoutedEventArgs e)
        => await VerifyEngineHostAsync(restart: false);

    private async void RestartEngineHostButton_Click(object sender, RoutedEventArgs e)
        => await VerifyEngineHostAsync(restart: true);

    private async void StopEngineHostButton_Click(object sender, RoutedEventArgs e)
    {
        _engineOperationInProgress = true;
        _engineStoppedByUser = true;
        _engineAuthenticated = false;
        SetEngineControlsBusy(true);
        SetEngineStatus("Parando", "Encerrando o processo local supervisionado.");
        RecordActivity(
            OperationalActivityLevel.Informational,
            "EngineHost",
            "Parada solicitada",
            "O usuário solicitou o encerramento da sessão local supervisionada.");

        try
        {
            if (_engineSupervisor is not null)
            {
                await _engineSupervisor.StopAsync();
            }

            SetEngineStatus(
                "Parado",
                "Processo supervisionado encerrado. Nenhuma sessão local é considerada ativa.");
            RecordActivity(
                OperationalActivityLevel.Success,
                "EngineHost",
                "Sessão encerrada",
                "O processo local supervisionado foi encerrado e a autenticação da sessão foi revogada.");
        }
        catch (Exception ex)
        {
            SetEngineStatus(
                "Bloqueado",
                $"A parada falhou de forma fechada ({ex.GetType().Name}). O supervisor será descartado.");
            RecordActivity(
                OperationalActivityLevel.Error,
                "EngineHost",
                "Falha ao encerrar sessão",
                $"A parada falhou de forma fechada ({ex.GetType().Name}); nenhum estado saudável foi preservado.");
            await DisposeEngineSupervisorAsync();
        }
        finally
        {
            _engineOperationInProgress = false;
            SetEngineControlsBusy(false);
            RefreshFirstRunView();
        }
    }

    private async Task VerifyEngineHostAsync(bool restart)
    {
        _engineVerificationAttempted = true;
        _engineOperationInProgress = true;
        _engineStoppedByUser = false;
        _engineAuthenticated = false;
        SetEngineControlsBusy(true);
        SetEngineStatus(
            restart ? "Reiniciando" : "Verificando",
            restart
                ? "Encerrando qualquer sessão anterior antes de iniciar uma nova sessão autenticada."
                : "Iniciando sessão local autenticada e executando Ping real.");
        RecordActivity(
            OperationalActivityLevel.Informational,
            "EngineHost",
            restart ? "Reinício solicitado" : "Verificação solicitada",
            restart
                ? "Uma nova sessão local será aceita somente depois de Ping autenticado."
                : "A aplicação iniciou uma tentativa de prova autenticada do EngineHost local.");

        try
        {
            _engineSupervisor ??= CreateEngineSupervisor();
            if (restart && _engineSupervisor.IsRunning)
            {
                await _engineSupervisor.StopAsync();
            }

            await _engineSupervisor.StartAsync();
            await RequireAuthenticatedPingAsync(_engineSupervisor);

            _engineAuthenticated = true;
            _lastAuthenticatedProbeUtc = DateTimeOffset.UtcNow;
            RenderAuthenticatedEngineState("Ping real confirmado. Supervisão contínua ativada.");
            RecordActivity(
                OperationalActivityLevel.Success,
                "EngineHost",
                "Sessão autenticada",
                _engineSupervisor.ProcessId is int processId
                    ? $"Ping autenticado confirmado para o processo local supervisionado PID {processId}."
                    : "Ping autenticado confirmado para o processo local supervisionado.");
        }
        catch (Exception ex)
        {
            SetEngineStatus(
                "Bloqueado",
                $"A verificação falhou de forma fechada ({ex.GetType().Name}). Nenhum estado saudável foi inferido.");
            RecordActivity(
                OperationalActivityLevel.Error,
                "EngineHost",
                "Verificação bloqueada",
                $"A prova autenticada falhou ({ex.GetType().Name}); a sessão local não foi aceita.");
            await DisposeEngineSupervisorAsync();
        }
        finally
        {
            _engineOperationInProgress = false;
            SetEngineControlsBusy(false);
            RefreshFirstRunView();
        }
    }

    private async void EngineHealthTimer_Tick(object? sender, object e)
    {
        if (_isClosing ||
            _engineOperationInProgress ||
            _engineHealthProbeInProgress ||
            !_engineAuthenticated ||
            _engineSupervisor is null ||
            _engineStoppedByUser)
        {
            return;
        }

        if (!_engineSupervisor.IsRunning)
        {
            _engineAuthenticated = false;
            SetEngineStatus(
                "Interrompido",
                "O processo supervisionado encerrou inesperadamente. A sessão autenticada foi revogada e exige nova verificação ou reinício.");
            RecordActivity(
                OperationalActivityLevel.Warning,
                "EngineHost",
                "Processo interrompido",
                "O supervisor detectou encerramento inesperado e revogou o estado autenticado da sessão.");
            await DisposeEngineSupervisorAsync();
            SetEngineControlsBusy(false);
            RefreshFirstRunView();
            return;
        }

        if (DateTimeOffset.UtcNow - _lastAuthenticatedProbeUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _engineHealthProbeInProgress = true;
        try
        {
            await RequireAuthenticatedPingAsync(_engineSupervisor);
            _lastAuthenticatedProbeUtc = DateTimeOffset.UtcNow;
            RenderAuthenticatedEngineState("Health-check autenticado confirmado automaticamente.");
        }
        catch (Exception ex)
        {
            _engineAuthenticated = false;
            SetEngineStatus(
                "Bloqueado",
                $"A supervisão automática perdeu a prova autenticada ({ex.GetType().Name}). A sessão foi invalidada.");
            RecordActivity(
                OperationalActivityLevel.Error,
                "EngineHost",
                "Prova de saúde perdida",
                $"O health-check autenticado falhou ({ex.GetType().Name}); a sessão foi invalidada de forma fechada.");
            await DisposeEngineSupervisorAsync();
        }
        finally
        {
            _engineHealthProbeInProgress = false;
            if (!_isClosing)
            {
                SetEngineControlsBusy(false);
                RefreshFirstRunView();
            }
        }
    }

    private static async Task RequireAuthenticatedPingAsync(EngineHostSupervisor supervisor)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var response = await supervisor.SendAsync(new EnginePingCommand(requestId));

        if (!response.Success ||
            !string.Equals(response.Code, "pong", StringComparison.Ordinal) ||
            !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("EngineHost returned an invalid authenticated Ping response.");
        }
    }

    private void VerifyLocalIntegrityButton_Click(object sender, RoutedEventArgs e)
    {
        _integrityAttempted = true;
        try
        {
            _localIntegrityResult = DesktopLocalIntegrityProbe.Probe(
                ("Desktop", typeof(MainWindow).Assembly.Location),
                ("Core", typeof(DesktopFirstRunReadiness).Assembly.Location),
                ("EngineHost", typeof(EngineHostRuntime).Assembly.Location));

            RecordActivity(
                _localIntegrityResult.Verified ? OperationalActivityLevel.Success : OperationalActivityLevel.Error,
                "Integridade",
                _localIntegrityResult.Verified ? "Estrutura local verificada" : "Estrutura local bloqueada",
                _localIntegrityResult.Detail);
        }
        catch (Exception ex)
        {
            _localIntegrityResult = new DesktopLocalIntegrityResult(
                false,
                $"A verificação estrutural falhou de forma fechada ({ex.GetType().Name}).",
                Array.Empty<DesktopIntegrityArtifact>());
            RecordActivity(
                OperationalActivityLevel.Error,
                "Integridade",
                "Verificação estrutural falhou",
                _localIntegrityResult.Detail);
        }

        RefreshFirstRunView();
    }

    private void PrepareDeviceIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var provisioner = new WindowsDeviceIdentityProvisioner();
            using var key = provisioner.GetOrCreateTpmKey(_firstRunProfile.InstallationId);
            _deviceSecurityTier = key.SecurityTierValue;
            RecordActivity(
                OperationalActivityLevel.Success,
                "Identidade",
                "Identidade TPM preparada",
                "Uma chave ECDSA P-256 não exportável vinculada ao provedor TPM foi comprovada para esta instalação.");
            OnboardingResultNotice.Severity = InfoBarSeverity.Success;
            OnboardingResultNotice.Title = "Identidade local pronta";
            OnboardingResultNotice.Message = "A chave TPM não exportável foi criada ou reaberta com sucesso. Nenhum fallback de software foi aplicado.";
            OnboardingResultNotice.IsOpen = true;
        }
        catch (Exception ex)
        {
            _deviceSecurityTier = null;
            RecordActivity(
                OperationalActivityLevel.Error,
                "Identidade",
                "Identidade TPM bloqueada",
                $"A identidade TPM não pôde ser comprovada ({ex.GetType().Name}); fallback de software não foi aplicado automaticamente.");
            OnboardingResultNotice.Severity = InfoBarSeverity.Error;
            OnboardingResultNotice.Title = "Identidade TPM indisponível";
            OnboardingResultNotice.Message = $"Falha fechada ({ex.GetType().Name}). O AEVRIX não reduziu automaticamente o tier de segurança.";
            OnboardingResultNotice.IsOpen = true;
        }

        RefreshFirstRunView();
    }

    private void OperatingModeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFirstRunPersistence)
        {
            return;
        }

        var mode = (OperatingModeInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "local" => DesktopOperatingMode.LocalSupervised,
            "remote" => DesktopOperatingMode.RemoteGoverned,
            _ => (DesktopOperatingMode?)null
        };

        _firstRunProfile = _firstRunProfile with
        {
            RequestedMode = mode,
            CompletedAtUtc = null
        };
        TrySaveFirstRunProfile();
        RecordActivity(
            OperationalActivityLevel.Informational,
            "Inicialização",
            "Modo operacional alterado",
            mode == DesktopOperatingMode.RemoteGoverned
                ? "Modo remoto governado selecionado; conclusão permanecerá bloqueada até endpoint, certificado e sessão serem comprovados."
                : mode == DesktopOperatingMode.LocalSupervised
                    ? "Modo local supervisionado selecionado; capacidades remotas permanecem indisponíveis."
                    : "Nenhum modo operacional selecionado.");
        RefreshFirstRunView();
    }

    private void PermissionsAcknowledgementCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFirstRunPersistence)
        {
            return;
        }

        _firstRunProfile = _firstRunProfile with
        {
            PermissionsAcknowledged = PermissionsAcknowledgementCheckBox.IsChecked == true,
            CompletedAtUtc = null
        };
        TrySaveFirstRunProfile();
        RefreshFirstRunView();
    }

    private void CompleteOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        var evaluation = GetFirstRunEvaluation();
        if (_firstRunProfileError is not null || !evaluation.CanComplete)
        {
            OnboardingResultNotice.Severity = InfoBarSeverity.Warning;
            OnboardingResultNotice.Title = "Inicialização ainda bloqueada";
            OnboardingResultNotice.Message = _firstRunProfileError is not null
                ? "O perfil persistido precisa ser reparado antes da conclusão."
                : evaluation.Summary;
            OnboardingResultNotice.IsOpen = true;
            return;
        }

        _firstRunProfile = _firstRunProfile with { CompletedAtUtc = DateTimeOffset.UtcNow };
        if (!TrySaveFirstRunProfile())
        {
            OnboardingResultNotice.Severity = InfoBarSeverity.Error;
            OnboardingResultNotice.Title = "Não foi possível persistir a conclusão";
            OnboardingResultNotice.Message = "O Desktop permanece em estado não concluído porque o perfil local não pôde ser gravado de forma confiável.";
            OnboardingResultNotice.IsOpen = true;
            return;
        }

        RecordActivity(
            OperationalActivityLevel.Success,
            "Inicialização",
            "First-run concluído",
            $"A configuração inicial foi concluída para o modo {_firstRunProfile.RequestedMode}. Estados operacionais continuam sujeitos a prova em cada sessão.");
        OnboardingResultNotice.Severity = InfoBarSeverity.Success;
        OnboardingResultNotice.Title = "Inicialização concluída";
        OnboardingResultNotice.Message = "A configuração inicial foi persistida. Isso não substitui os health-checks e gates de runtime de cada sessão.";
        OnboardingResultNotice.IsOpen = true;
        RefreshFirstRunView();
    }

    private void ResetFirstRunProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var replacement = DesktopFirstRunProfile.CreateNew();
        try
        {
            _firstRunProfileStore.Save(replacement);
            _firstRunProfile = replacement;
            _firstRunProfileError = null;
            _deviceSecurityTier = null;
            _deviceCertificateValidated = false;
            _remoteSessionAuthenticated = false;
            InitializeFirstRunControls();
            RecordActivity(
                OperationalActivityLevel.Warning,
                "Inicialização",
                "Perfil local recriado",
                "O perfil persistido de first-run foi substituído por um estado limpo; nenhuma confiança anterior foi reaproveitada.");
        }
        catch (Exception ex)
        {
            _firstRunProfileError = ex;
            RecordActivity(
                OperationalActivityLevel.Error,
                "Inicialização",
                "Falha ao recriar perfil",
                $"O perfil local não pôde ser recriado ({ex.GetType().Name}).");
        }

        RefreshFirstRunView();
    }

    private void SettingsRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        TryValidatePersistedDeviceCertificate();
        RefreshFirstRunView();
    }

    private void InitializeFirstRunControls()
    {
        _suppressFirstRunPersistence = true;
        try
        {
            OperatingModeInput.SelectedIndex = _firstRunProfile.RequestedMode switch
            {
                DesktopOperatingMode.LocalSupervised => 0,
                DesktopOperatingMode.RemoteGoverned => 1,
                _ => -1
            };
            PermissionsAcknowledgementCheckBox.IsChecked = _firstRunProfile.PermissionsAcknowledged;
        }
        finally
        {
            _suppressFirstRunPersistence = false;
        }
    }

    private void TryValidatePersistedDeviceCertificate()
    {
        _deviceCertificateValidated = false;
        if (string.IsNullOrWhiteSpace(_firstRunProfile.DeviceCertificateThumbprint))
        {
            return;
        }

        try
        {
            var provider = new WindowsDeviceCertificateProvider();
            using var certificate = provider.LoadByThumbprint(_firstRunProfile.DeviceCertificateThumbprint);
            _deviceCertificateValidated = true;
        }
        catch (Exception ex)
        {
            RecordActivity(
                OperationalActivityLevel.Warning,
                "Identidade",
                "Certificado persistido não validado",
                $"O certificado de dispositivo referenciado pelo perfil não foi aceito nesta sessão ({ex.GetType().Name}).");
        }
    }

    private bool TrySaveFirstRunProfile()
    {
        if (_firstRunProfileError is not null)
        {
            return false;
        }

        try
        {
            _firstRunProfileStore.Save(_firstRunProfile);
            return true;
        }
        catch (Exception ex)
        {
            _firstRunProfileError = ex;
            return false;
        }
    }

    private DesktopFirstRunEvaluation GetFirstRunEvaluation()
    {
        var signals = new DesktopFirstRunSignals(
            StructuralIntegrityAttempted: _integrityAttempted,
            StructuralIntegrityVerified: _localIntegrityResult?.Verified == true,
            EngineHostVerificationAttempted: _engineVerificationAttempted,
            EngineHostAuthenticated: _engineAuthenticated,
            DeviceSecurityTier: _deviceSecurityTier,
            DeviceCertificateValidated: _deviceCertificateValidated,
            RemoteEndpointConfigured: !string.IsNullOrWhiteSpace(_firstRunProfile.RemoteBaseUri),
            RemoteSessionAuthenticated: _remoteSessionAuthenticated,
            RequestedMode: _firstRunProfile.RequestedMode,
            PermissionsAcknowledged: _firstRunProfile.PermissionsAcknowledged);

        return DesktopFirstRunReadiness.Evaluate(signals);
    }

    private void RefreshFirstRunView()
    {
        var evaluation = GetFirstRunEvaluation();
        RenderGate(evaluation.Gate("integrity"), IntegrityGateStatusText, IntegrityGateDetailText);
        RenderGate(evaluation.Gate("enginehost"), EngineGateStatusText, EngineGateDetailText);
        RenderGate(evaluation.Gate("device-identity"), DeviceIdentityGateStatusText, DeviceIdentityGateDetailText);
        RenderGate(evaluation.Gate("operating-mode"), OperatingModeGateStatusText, OperatingModeGateDetailText);
        RenderGate(evaluation.Gate("remote-identity"), RemoteIdentityGateStatusText, RemoteIdentityGateDetailText);
        RenderGate(evaluation.Gate("permissions"), PermissionsGateStatusText, PermissionsGateDetailText);

        OnboardingSummaryText.Text = _firstRunProfileError is null
            ? evaluation.Summary
            : $"Perfil local bloqueado ({_firstRunProfileError.GetType().Name}). Recrie o perfil antes de concluir.";
        CompleteOnboardingButton.IsEnabled = _firstRunProfileError is null && evaluation.CanComplete;

        FirstRunProfileNotice.IsOpen = _firstRunProfileError is not null;
        if (_firstRunProfileError is not null)
        {
            FirstRunProfileNotice.Message = $"O perfil persistido falhou na validação ({_firstRunProfileError.GetType().Name}). Nenhuma configuração anterior foi tratada como confiável.";
        }
        ResetFirstRunProfileButton.Visibility = _firstRunProfileError is null ? Visibility.Collapsed : Visibility.Visible;

        CommandCenterOnboardingStatusText.Text = _firstRunProfile.CompletedAtUtc is { } completed
            ? $"Concluída • {completed.ToLocalTime():dd/MM/yyyy HH:mm}"
            : evaluation.CanComplete
                ? "Pronta para concluir"
                : "Pendente / bloqueada";

        RefreshSettingsView();
    }

    private static void RenderGate(
        DesktopReadinessGate gate,
        TextBlock statusText,
        TextBlock detailText)
    {
        statusText.Text = gate.Status switch
        {
            DesktopReadinessStatus.Ready => "PRONTO",
            DesktopReadinessStatus.Blocked => "BLOQUEADO",
            _ => "PENDENTE"
        };
        detailText.Text = gate.Detail;
    }

    private void RefreshSettingsView()
    {
        SettingsInstallationIdText.Text = $"Installation ID: {_firstRunProfile.InstallationId}";
        SettingsCompletionText.Text = _firstRunProfile.CompletedAtUtc is { } completed
            ? $"First-run concluído em {completed.ToLocalTime():dd/MM/yyyy HH:mm}."
            : "First-run ainda não concluído.";
        SettingsModeText.Text = _firstRunProfile.RequestedMode switch
        {
            DesktopOperatingMode.LocalSupervised => "Local supervisionado",
            DesktopOperatingMode.RemoteGoverned => "Remoto governado",
            _ => "Não selecionado"
        };
        SettingsIdentityText.Text = _deviceSecurityTier switch
        {
            DeviceKeySecurityTier.TpmNonExportable => "TPM não exportável comprovado nesta sessão.",
            DeviceKeySecurityTier.SoftwareNonExportable => "Software não exportável comprovado nesta sessão.",
            _ when _deviceCertificateValidated => "Certificado do dispositivo validado; tier da chave local ainda não foi reprovado nesta sessão.",
            _ => "Identidade local não comprovada nesta sessão."
        };
        SettingsEngineText.Text = _engineAuthenticated
            ? "EngineHost autenticado nesta sessão."
            : _engineVerificationAttempted
                ? "EngineHost sem prova autenticada válida nesta sessão."
                : "EngineHost ainda não verificado nesta sessão.";
        SettingsRemoteText.Text = _remoteSessionAuthenticated
            ? "Sessão remota autenticada."
            : !string.IsNullOrWhiteSpace(_firstRunProfile.RemoteBaseUri)
                ? "Endpoint configurado, porém sessão remota não autenticada."
                : "Endpoint remoto não configurado; sessão remota indisponível.";
    }

    private void RenderAuthenticatedEngineState(string message)
    {
        var detail = _engineSupervisor?.ProcessId is int processId
            ? $"{message} Processo local supervisionado: PID {processId}."
            : message;

        SetEngineStatus("Autenticado", detail);
    }

    private void SetEngineStatus(string status, string detail)
    {
        EngineHostStatusText.Text = status;
        EngineHostDetailText.Text = detail;
        MissionEngineStatusText.Text = status;
        MissionEngineDetailText.Text = detail;
    }

    private void SetEngineControlsBusy(bool busy)
    {
        VerifyEngineHostButton.IsEnabled = !busy;
        RestartEngineHostButton.IsEnabled = !busy;
        StopEngineHostButton.IsEnabled = !busy && _engineSupervisor?.IsRunning == true;
        OnboardingVerifyEngineButton.IsEnabled = !busy;
        SettingsVerifyEngineButton.IsEnabled = !busy;
    }

    private async Task DisposeEngineSupervisorAsync()
    {
        _engineAuthenticated = false;

        if (_engineSupervisor is null)
        {
            return;
        }

        await _engineSupervisor.DisposeAsync();
        _engineSupervisor = null;
    }

    private void ValidateScopeButton_Click(object sender, RoutedEventArgs e)
    {
        PolicyUnavailableNotice.IsOpen = true;
        RecordActivity(
            OperationalActivityLevel.Warning,
            "Política",
            "Validação indisponível",
            "A superfície informou que o motor real de políticas ainda não está conectado; nenhuma missão foi criada.");
    }

    private void BackToHomeButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = HomeNavItem;
        ShowSection("home", "Command Center");
    }

    private static EngineHostSupervisor CreateEngineSupervisor()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        return new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _engineHealthTimer.Stop();
        await DisposeEngineSupervisorAsync();
    }

    private void RecordActivity(
        OperationalActivityLevel level,
        string source,
        string title,
        string detail)
    {
        _activityJournal.Append(level, source, title, detail);
        RefreshActivityView();
    }

    private void RefreshActivityView()
    {
        var entries = _activityJournal.Snapshot();
        var displayEntries = entries
            .Select(FormatActivityEntry)
            .ToArray();

        ActivityListView.ItemsSource = displayEntries;
        ActivityEmptyStateText.Visibility = displayEntries.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatActivityEntry(OperationalActivityEntry entry)
    {
        var level = entry.Level switch
        {
            OperationalActivityLevel.Success => "OK",
            OperationalActivityLevel.Warning => "ATENÇÃO",
            OperationalActivityLevel.Error => "ERRO",
            _ => "INFO"
        };

        var localTimestamp = entry.TimestampUtc.ToLocalTime();
        return $"{localTimestamp:HH:mm:ss} • {level} • {entry.Source} — {entry.Title}\n{entry.Detail}";
    }

    private void ShowSection(string route, string title)
    {
        var showHome = string.Equals(route, "home", StringComparison.Ordinal);
        var showOnboarding = string.Equals(route, "onboarding", StringComparison.Ordinal);
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);
        var showMission = string.Equals(route, "mission", StringComparison.Ordinal);
        var showActivity = string.Equals(route, "activity", StringComparison.Ordinal);
        var showSettings = string.Equals(route, "settings", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        OnboardingView.Visibility = showOnboarding ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        MissionControlView.Visibility = showMission ? Visibility.Visible : Visibility.Collapsed;
        ActivityView.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showOnboarding && !showNew && !showMission && !showActivity && !showSettings
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showActivity)
        {
            RefreshActivityView();
        }
        if (showOnboarding || showSettings || showHome)
        {
            RefreshFirstRunView();
        }

        if (!showHome && !showOnboarding && !showNew && !showMission && !showActivity && !showSettings)
        {
            PlannedSectionTitle.Text = title;
        }
    }
}
