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
        EngineHostStatusText.Text = "Parando";
        EngineHostDetailText.Text = "Encerrando o processo local supervisionado.";

        try
        {
            if (_engineSupervisor is not null)
            {
                await _engineSupervisor.StopAsync();
            }

            EngineHostStatusText.Text = "Parado";
            EngineHostDetailText.Text = "Processo supervisionado encerrado. Nenhuma sessão local é considerada ativa.";
        }
        catch (Exception ex)
        {
            EngineHostStatusText.Text = "Bloqueado";
            EngineHostDetailText.Text = $"A parada falhou de forma fechada ({ex.GetType().Name}). O supervisor será descartado.";
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
        EngineHostStatusText.Text = restart ? "Reiniciando" : "Verificando";
        EngineHostDetailText.Text = restart
            ? "Encerrando qualquer sessão anterior antes de iniciar uma nova sessão autenticada."
            : "Iniciando sessão local autenticada e executando Ping real.";

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
        }
        catch (Exception ex)
        {
            EngineHostStatusText.Text = "Bloqueado";
            EngineHostDetailText.Text = $"A verificação falhou de forma fechada ({ex.GetType().Name}). Nenhum estado saudável foi inferido.";
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
            EngineHostStatusText.Text = "Interrompido";
            EngineHostDetailText.Text = "O processo supervisionado encerrou inesperadamente. A sessão autenticada foi revogada e exige nova verificação ou reinício.";
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
            EngineHostStatusText.Text = "Bloqueado";
            EngineHostDetailText.Text = $"A supervisão automática perdeu a prova autenticada ({ex.GetType().Name}). A sessão foi invalidada.";
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
        EngineHostStatusText.Text = "Autenticado";
        EngineHostDetailText.Text = _engineSupervisor?.ProcessId is int processId
            ? $"{message} Processo local supervisionado: PID {processId}."
            : message;
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

    private void ShowSection(string route, string title)
    {
        var showHome = string.Equals(route, "home", StringComparison.Ordinal);
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showNew
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!showHome && !showNew)
        {
            PlannedSectionTitle.Text = title;
        }
    }
}
