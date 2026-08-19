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
    private bool _firstRunReady;
    private bool _integrityAttempted;
    private bool _engineVerificationAttempted;
    private bool _deviceCertificateValidated;
    private bool _remoteSessionAuthenticated;
    private bool _suppressFirstRunPersistence;
    private bool _engineAuthenticated;
    private bool _engineOperationInProgress;
    private bool _engineHealthProbeInProgress;
    private bool _engineStoppedByUser;
    private bool _guidedBootstrapStarted;
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

        ApplyRecommendedDefaults();
        InitializeFirstRunControls();
        _firstRunReady = true;
        TryValidatePersistedDeviceCertificate();

        _engineHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _engineHealthTimer.Tick += EngineHealthTimer_Tick;
        _engineHealthTimer.Start();

        SetEngineStatus(
            "Preparando",
            "O AEVRIX verificará automaticamente o motor local nesta sessão.");
        RecordActivity(
            OperationalActivityLevel.Informational,
            "Desktop",
            "Sessão iniciada",
            "Shell Windows carregado. A preparação segura local será executada automaticamente; decisões de autorização permanecem explícitas.");

        RefreshFirstRunView();
        RootNavigation.SelectedItem = HomeNavItem;
        ShowSection("home", "Visão geral");
        _ = RunGuidedBootstrapAsync();
    }

    private void ApplyRecommendedDefaults()
    {
        var profileChanged = false;
        if (_firstRunProfile.RequestedMode is null)
        {
            _firstRunProfile = _firstRunProfile with
            {
                RequestedMode = DesktopOperatingMode.LocalSupervised,
                CompletedAtUtc = null
            };
            profileChanged = true;
        }

        if (profileChanged)
        {
            TrySaveFirstRunProfile();
            RecordActivity(
                OperationalActivityLevel.Informational,
                "Inicialização",
                "Padrão seguro aplicado",
                "Modo local supervisionado selecionado automaticamente. O usuário pode alterá-lo a qualquer momento.");
        }

        if (string.IsNullOrWhiteSpace(WorkspaceInput.Text))
        {
            WorkspaceInput.Text = $"investigacao-{DateTime.Now:yyyyMMdd-HHmm}";
        }

        if (SensitivityInput.SelectedIndex < 0)
        {
            SensitivityInput.SelectedIndex = 0;
        }
    }

    private async Task RunGuidedBootstrapAsync()
    {
        if (_guidedBootstrapStarted || _isClosing)
        {
            return;
        }

        _guidedBootstrapStarted = true;
        await Task.Yield();

        if (!_integrityAttempted)
        {
            RunLocalIntegrityCheck();
        }

        if (_deviceSecurityTier is null)
        {
            PrepareDeviceIdentity(showNotice: false);
        }

        if (!_engineAuthenticated && !_engineOperationInProgress && !_isClosing)
        {
            await VerifyEngineHostAsync(restart: false);
        }

        if (!_isClosing)
        {
            RefreshFirstRunView();
        }
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (!_firstRunReady)
        {
            return;
        }

        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            ShowSection("home", "Visão geral");
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
        ShowSection("onboarding", "Configuração inicial");
    }

    private void OpenMissionControlButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = MissionControlNavItem;
        ShowSection("mission", "Execução e missões");
    }

    private void OpenActivityButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = ActivityNavItem;
        ShowSection("activity", "Histórico");
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
            RenderAuthenticatedEngineState("Motor local pronto e supervisionado.");
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
                "Precisa de atenção",
                $"A verificação falhou de forma fechada ({ex.GetType().Name}). Use Verificar novamente para tentar outra vez.");
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
                "O motor local encerrou inesperadamente. Use Verificar ou Reiniciar para recuperar a sessão.");
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
            RenderAuthenticatedEngineState("Motor local saudável e supervisionado.");
        }
        catch (Exception ex)
        {
            _engineAuthenticated = false;
            SetEngineStatus(
                "Precisa de atenção",
                $"A supervisão perdeu a prova autenticada ({ex.GetType().Name}). A sessão foi invalidada com segurança.");
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
        => RunLocalIntegrityCheck();

    private void RunLocalIntegrityCheck()
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
        => PrepareDeviceIdentity(showNotice: true);

    private void PrepareDeviceIdentity(bool showNotice)
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
            if (showNotice)
            {
                OnboardingResultNotice.Severity = InfoBarSeverity.Success;
                OnboardingResultNotice.Title = "Identidade local pronta";
                OnboardingResultNotice.Message = "A chave TPM não exportável foi criada ou reaberta com sucesso. Nenhum fallback de software foi aplicado.";
                OnboardingResultNotice.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            _deviceSecurityTier = null;
            RecordActivity(
                OperationalActivityLevel.Error,
                "Identidade",
                "Identidade TPM bloqueada",
                $"A identidade TPM não pôde ser comprovada ({ex.GetType().Name}); fallback de software não foi aplicado automaticamente.");
            if (showNotice)
            {
                OnboardingResultNotice.Severity = InfoBarSeverity.Error;
                OnboardingResultNotice.Title = "Identidade TPM indisponível";
                OnboardingResultNotice.Message = $"Falha fechada ({ex.GetType().Name}). O AEVRIX não reduziu automaticamente o tier de segurança.";
                OnboardingResultNotice.IsOpen = true;
            }
        }

        RefreshFirstRunView();
    }

    private void OperatingModeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_firstRunReady || _suppressFirstRunPersistence)
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
        if (!_firstRunReady || _suppressFirstRunPersistence)
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
            OnboardingResultNotice.Title = "Ainda falta uma etapa";
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
            OnboardingResultNotice.Title = "Não foi possível salvar a configuração";
            OnboardingResultNotice.Message = "O AEVRIX permanece em estado não concluído porque o perfil local não pôde ser gravado de forma confiável.";
            OnboardingResultNotice.IsOpen = true;
            return;
        }

        RecordActivity(
            OperationalActivityLevel.Success,
            "Inicialização",
            "Configuração inicial concluída",
            $"A configuração inicial foi concluída para o modo {_firstRunProfile.RequestedMode}. Estados operacionais continuam sujeitos a prova em cada sessão.");
        OnboardingResultNotice.Severity = InfoBarSeverity.Success;
        OnboardingResultNotice.Title = "Pronto para começar";
        OnboardingResultNotice.Message = "A configuração deste PC foi concluída. Você já pode iniciar uma nova investigação.";
        OnboardingResultNotice.IsOpen = true;
        RefreshFirstRunView();
    }

    private void ResetFirstRunProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var replacement = DesktopFirstRunProfile.CreateNew() with
        {
            RequestedMode = DesktopOperatingMode.LocalSupervised
        };
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
                "O perfil persistido foi substituído por um estado limpo com modo local supervisionado como padrão recomendado.");
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
        if (!_firstRunReady)
        {
            return;
        }

        var evaluation = GetFirstRunEvaluation();
        RenderGate(evaluation.Gate("integrity"), IntegrityGateStatusText, IntegrityGateDetailText);
        RenderGate(evaluation.Gate("enginehost"), EngineGateStatusText, EngineGateDetailText);
        RenderGate(evaluation.Gate("device-identity"), DeviceIdentityGateStatusText, DeviceIdentityGateDetailText);
        RenderGate(evaluation.Gate("operating-mode"), OperatingModeGateStatusText, OperatingModeGateDetailText);
        RenderGate(evaluation.Gate("remote-identity"), RemoteIdentityGateStatusText, RemoteIdentityGateDetailText);
        RenderGate(evaluation.Gate("permissions"), PermissionsGateStatusText, PermissionsGateDetailText);

        OnboardingSummaryText.Text = _firstRunProfileError is null
            ? evaluation.Summary
            : $"O perfil local precisa ser reparado ({_firstRunProfileError.GetType().Name}).";
        CompleteOnboardingButton.IsEnabled = _firstRunProfileError is null && evaluation.CanComplete;

        FirstRunProfileNotice.IsOpen = _firstRunProfileError is not null;
        if (_firstRunProfileError is not null)
        {
            FirstRunProfileNotice.Message = $"O perfil persistido falhou na validação ({_firstRunProfileError.GetType().Name}). Nenhuma configuração anterior foi tratada como confiável.";
        }
        ResetFirstRunProfileButton.Visibility = _firstRunProfileError is null ? Visibility.Collapsed : Visibility.Visible;

        var completed = _firstRunProfile.CompletedAtUtc is not null;
        CommandCenterOnboardingStatusText.Text = _firstRunProfile.CompletedAtUtc is { } completedAt
            ? $"Pronta • {completedAt.ToLocalTime():dd/MM HH:mm}"
            : evaluation.CanComplete
                ? "Só falta confirmar"
                : _guidedBootstrapStarted
                    ? "Preparando automaticamente"
                    : "Preparação pendente";

        CommandCenterModeText.Text = _firstRunProfile.RequestedMode switch
        {
            DesktopOperatingMode.RemoteGoverned => "Remoto governado",
            _ => "Local supervisionado"
        };

        if (_firstRunProfileError is not null)
        {
            CommandCenterNextStepTitleText.Text = "A configuração precisa de atenção";
            CommandCenterNextStepDetailText.Text = "Abra a configuração inicial para reparar o perfil local antes de continuar.";
            CommandCenterSetupButton.Content = "Corrigir configuração";
            CommandCenterSetupButton.Visibility = Visibility.Visible;
            StartAnalysisButton.IsEnabled = false;
        }
        else if (completed)
        {
            CommandCenterNextStepTitleText.Text = "Pronto para começar uma investigação";
            CommandCenterNextStepDetailText.Text = "Informe o alvo e o objetivo. O AEVRIX já manterá o workspace, a política de dados e o ambiente local nos padrões recomendados.";
            CommandCenterSetupButton.Visibility = Visibility.Collapsed;
            StartAnalysisButton.IsEnabled = true;
        }
        else if (evaluation.CanComplete)
        {
            CommandCenterNextStepTitleText.Text = "Só falta sua confirmação";
            CommandCenterNextStepDetailText.Text = "As verificações automáticas já estão prontas. Revise a configuração e confirme a postura de permissões para liberar o fluxo de investigação.";
            CommandCenterSetupButton.Content = "Revisar e concluir";
            CommandCenterSetupButton.Visibility = Visibility.Visible;
            StartAnalysisButton.IsEnabled = false;
        }
        else
        {
            CommandCenterNextStepTitleText.Text = "Preparando este computador";
            CommandCenterNextStepDetailText.Text = "O AEVRIX verifica integridade, identidade TPM e EngineHost automaticamente. Se alguma etapa exigir você, ela aparecerá aqui.";
            CommandCenterSetupButton.Content = "Acompanhar preparação";
            CommandCenterSetupButton.Visibility = Visibility.Visible;
            StartAnalysisButton.IsEnabled = false;
        }

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
            DesktopReadinessStatus.Blocked => "PRECISA DE ATENÇÃO",
            _ => "PENDENTE"
        };
        detailText.Text = gate.Detail;
    }

    private void RefreshSettingsView()
    {
        SettingsInstallationIdText.Text = $"Installation ID: {_firstRunProfile.InstallationId}";
        SettingsCompletionText.Text = _firstRunProfile.CompletedAtUtc is { } completed
            ? $"Configuração inicial concluída em {completed.ToLocalTime():dd/MM/yyyy HH:mm}."
            : "Configuração inicial ainda não concluída.";
        SettingsModeText.Text = _firstRunProfile.RequestedMode switch
        {
            DesktopOperatingMode.LocalSupervised => "Local supervisionado (recomendado)",
            DesktopOperatingMode.RemoteGoverned => "Remoto governado",
            _ => "Local supervisionado (recomendado)"
        };
        SettingsIdentityText.Text = _deviceSecurityTier switch
        {
            DeviceKeySecurityTier.TpmNonExportable => "TPM não exportável comprovado nesta sessão.",
            DeviceKeySecurityTier.SoftwareNonExportable => "Software não exportável comprovado nesta sessão.",
            _ when _deviceCertificateValidated => "Certificado do dispositivo validado; tier da chave local ainda não foi comprovado nesta sessão.",
            _ => "Identidade local ainda não comprovada nesta sessão."
        };
        SettingsEngineText.Text = _engineAuthenticated
            ? "Motor local autenticado e supervisionado nesta sessão."
            : _engineVerificationAttempted
                ? "Motor local sem prova autenticada válida nesta sessão."
                : "Motor local ainda não verificado nesta sessão.";
        SettingsRemoteText.Text = _remoteSessionAuthenticated
            ? "Sessão remota autenticada."
            : !string.IsNullOrWhiteSpace(_firstRunProfile.RemoteBaseUri)
                ? "Endpoint configurado, porém sessão remota não autenticada."
                : "Não configurados; não são necessários no modo local supervisionado.";
    }

    private void RenderAuthenticatedEngineState(string message)
    {
        var detail = _engineSupervisor?.ProcessId is int processId
            ? $"{message} Processo local supervisionado: PID {processId}."
            : message;

        SetEngineStatus("Pronto", detail);
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
        ShowSection("home", "Visão geral");
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
        if (_firstRunReady && (showOnboarding || showSettings || showHome))
        {
            RefreshFirstRunView();
        }

        if (!showHome && !showOnboarding && !showNew && !showMission && !showActivity && !showSettings)
        {
            PlannedSectionTitle.Text = title;
        }
    }
}
