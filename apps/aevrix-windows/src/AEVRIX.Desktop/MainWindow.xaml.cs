using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopEngineSession _engineSession = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _engineRefreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Title = "AEVRIX Desktop";
        ShowSection("home", "Command Center");
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshEngineStatusAsync();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _lifetimeCts.Cancel();
        try
        {
            await _engineSession.DisposeAsync();
        }
        finally
        {
            _lifetimeCts.Dispose();
        }
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

    private async void RefreshEngineStatusButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEngineStatusAsync();
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

    private async Task RefreshEngineStatusAsync()
    {
        if (_engineRefreshInProgress || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        _engineRefreshInProgress = true;
        RefreshEngineStatusButton.IsEnabled = false;
        EngineStatusProgress.IsActive = true;
        EngineStatusProgress.Visibility = Visibility.Visible;
        EngineStatusText.Text = "Verificando…";
        EngineStatusDetail.Text = "Iniciando sessão local autenticada e consultando o estado canônico do EngineHost.";

        try
        {
            var status = await _engineSession.RefreshAsync(_lifetimeCts.Token);
            if (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            EngineStatusText.Text = status.State;
            EngineStatusDetail.Text = status.Detail;
            EngineStatusLastCheckedText.Text = $"Última prova local: {DateTimeOffset.Now:dd/MM/yyyy HH:mm:ss zzz}";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown intentionally cancels any in-flight readiness probe.
        }
        finally
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                RefreshEngineStatusButton.IsEnabled = true;
                EngineStatusProgress.IsActive = false;
                EngineStatusProgress.Visibility = Visibility.Collapsed;
            }
            _engineRefreshInProgress = false;
        }
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
