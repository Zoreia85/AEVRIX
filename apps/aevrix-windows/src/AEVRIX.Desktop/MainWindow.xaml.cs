using System;
using AEVRIX.Desktop.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopEngineSession _engineSession = new();
    private readonly DispatcherTimer _engineHealthTimer;
    private bool _engineVerified;
    private bool _healthProbeInProgress;
    private FirstRunView? _firstRunView;
    private ProjectsView? _projectsView;
    private EvidenceView? _evidenceView;
    private BlueprintView? _blueprintView;

    public MainWindow()
    {
        InitializeComponent();
        Title = "AEVRIX Desktop";
        Closed += MainWindow_Closed;
        _engineHealthTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
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
        SetEngineControlsBusy(true);
        _engineVerified = false;
        EngineHostStatusText.Text = "Parando";
        EngineHostDetailText.Text = "Encerrando a sessão local supervisionada.";

        try
        {
            var status = await _engineSession.StopAsync();
            ApplyEngineStatus(status);
        }
        catch (Exception ex)
        {
            RevokeEngineState($"A parada falhou de forma fechada ({ex.GetType().Name}). Nenhum estado saudável foi mantido.");
        }
        finally
        {
            SetEngineControlsBusy(false);
        }
    }

    private async Task VerifyEngineHostAsync(bool restart)
    {
        SetEngineControlsBusy(true);
        _engineVerified = false;
        EngineHostStatusText.Text = restart ? "Reiniciando" : "Verificando";
        EngineHostDetailText.Text = restart
            ? "Encerrando a sessão anterior antes de exigir um novo estado engine_ready autenticado."
            : "Iniciando sessão local autenticada e exigindo o estado canônico engine_ready.";

        try
        {
            var status = restart
                ? await _engineSession.RestartAsync()
                : await _engineSession.RefreshAsync();
            ApplyEngineStatus(status);
        }
        catch (OperationCanceledException)
        {
            RevokeEngineState("A verificação foi cancelada. Nenhum estado saudável foi inferido.");
        }
        catch (Exception ex)
        {
            RevokeEngineState($"A verificação falhou de forma fechada ({ex.GetType().Name}). Nenhum estado saudável foi inferido.");
        }
        finally
        {
            SetEngineControlsBusy(false);
        }
    }

    private void ApplyEngineStatus(DesktopEngineStatus status)
    {
        _engineVerified = status.Verified;
        EngineHostStatusText.Text = status.State;
        EngineHostDetailText.Text = status.Detail;
        StopEngineHostButton.IsEnabled = _engineSession.IsRunning;
        _firstRunView?.SetEngineStatus(status.State, status.Detail);
    }

    private void RevokeEngineState(string detail)
    {
        _engineVerified = false;
        EngineHostStatusText.Text = "Bloqueado";
        EngineHostDetailText.Text = detail;
        StopEngineHostButton.IsEnabled = _engineSession.IsRunning;
        _firstRunView?.SetEngineStatus("Bloqueado", detail);
    }

    private void SetEngineControlsBusy(bool busy)
    {
        VerifyEngineHostButton.IsEnabled = !busy;
        RestartEngineHostButton.IsEnabled = !busy;
        StopEngineHostButton.IsEnabled = !busy && _engineSession.IsRunning;
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

    private async void EngineHealthTimer_Tick(object? sender, object e)
    {
        if (!_engineVerified || _healthProbeInProgress)
        {
            return;
        }

        if (!_engineSession.IsRunning)
        {
            RevokeEngineState(
                "A sessão autenticada do EngineHost deixou de estar ativa. O estado saudável foi revogado automaticamente.");
            return;
        }

        _healthProbeInProgress = true;
        try
        {
            var status = await _engineSession.RefreshAsync();
            if (!status.Verified)
            {
                ApplyEngineStatus(status);
            }
        }
        catch (Exception ex)
        {
            RevokeEngineState(
                $"A revalidação autenticada do EngineHost falhou ({ex.GetType().Name}). O estado saudável foi revogado.");
        }
        finally
        {
            _healthProbeInProgress = false;
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _engineHealthTimer.Stop();
        _engineVerified = false;
        await _engineSession.DisposeAsync();
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

        HideProductSurfaces();
        if (showHome || showNew)
        {
            return;
        }

        var surface = GetOrCreateProductSurface(route);
        if (surface is not null)
        {
            PlannedSectionPlaceholder.Visibility = Visibility.Collapsed;
            surface.Visibility = Visibility.Visible;
            return;
        }

        PlannedSectionPlaceholder.Visibility = Visibility.Visible;
        PlannedSectionTitle.Text = title;
    }

    private FrameworkElement? GetOrCreateProductSurface(string route)
    {
        switch (route)
        {
            case "first-run":
                if (_firstRunView is null)
                {
                    _firstRunView = new FirstRunView();
                    _firstRunView.SetEngineStatus(
                        _engineVerified ? EngineHostStatusText.Text : "Não verificado",
                        _engineVerified
                            ? EngineHostDetailText.Text
                            : "O Command Center ainda não possui uma prova engine_ready ativa nesta sessão.");
                    PlannedSectionView.Children.Add(_firstRunView);
                }
                return _firstRunView;

            case "projects":
                if (_projectsView is null)
                {
                    _projectsView = new ProjectsView();
                    PlannedSectionView.Children.Add(_projectsView);
                }
                return _projectsView;

            case "evidence":
                if (_evidenceView is null)
                {
                    _evidenceView = new EvidenceView();
                    PlannedSectionView.Children.Add(_evidenceView);
                }
                return _evidenceView;

            case "blueprint":
                if (_blueprintView is null)
                {
                    _blueprintView = new BlueprintView();
                    PlannedSectionView.Children.Add(_blueprintView);
                }
                return _blueprintView;

            default:
                return null;
        }
    }

    private void HideProductSurfaces()
    {
        if (_firstRunView is not null) _firstRunView.Visibility = Visibility.Collapsed;
        if (_projectsView is not null) _projectsView.Visibility = Visibility.Collapsed;
        if (_evidenceView is not null) _evidenceView.Visibility = Visibility.Collapsed;
        if (_blueprintView is not null) _blueprintView.Visibility = Visibility.Collapsed;
    }
}
