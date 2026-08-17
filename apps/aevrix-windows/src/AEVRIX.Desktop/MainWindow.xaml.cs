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
    private EngineHostSupervisor? _engineSupervisor;
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
        ShowSection("home", "Command Center");
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
        }
    }

    private async Task VerifyEngineHostAsync(bool restart)
    {
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
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);
        var showMission = string.Equals(route, "mission", StringComparison.Ordinal);
        var showActivity = string.Equals(route, "activity", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        MissionControlView.Visibility = showMission ? Visibility.Visible : Visibility.Collapsed;
        ActivityView.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showNew && !showMission && !showActivity
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showActivity)
        {
            RefreshActivityView();
        }

        if (!showHome && !showNew && !showMission && !showActivity)
        {
            PlannedSectionTitle.Text = title;
        }
    }
}
