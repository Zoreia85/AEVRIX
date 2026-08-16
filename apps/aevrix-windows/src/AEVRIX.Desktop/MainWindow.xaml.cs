using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopEngineSession _engineSession = new();
    private readonly DesktopFirstRunService _firstRunService = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _engineRefreshInProgress;
    private bool _firstRunIdentityInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Title = "AEVRIX Desktop";
        ShowSection("home", "Command Center");
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadFirstRunStateAsync();
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

    private async void PrepareDeviceIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        await PrepareOrVerifyDeviceIdentityAsync();
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

    private async Task LoadFirstRunStateAsync()
    {
        if (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        var state = await _firstRunService.ReadLocalStateAsync(_lifetimeCts.Token);
        if (!_lifetimeCts.IsCancellationRequested)
        {
            ApplyFirstRunIdentityState(state);
        }
    }

    private async Task PrepareOrVerifyDeviceIdentityAsync()
    {
        if (_firstRunIdentityInProgress || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        _firstRunIdentityInProgress = true;
        PrepareDeviceIdentityButton.IsEnabled = false;
        FirstRunIdentityProgress.IsActive = true;
        FirstRunIdentityProgress.Visibility = Visibility.Visible;
        FirstRunIdentityStateText.Text = "Verificando…";
        FirstRunIdentityDetailText.Text = "Validando suporte TPM/CNG e a chave ECDSA P-256 não exportável.";

        try
        {
            var state = await _firstRunService.PrepareOrVerifyTpmIdentityAsync(_lifetimeCts.Token);
            if (!_lifetimeCts.IsCancellationRequested)
            {
                ApplyFirstRunIdentityState(state);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown intentionally cancels first-run work.
        }
        finally
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                PrepareDeviceIdentityButton.IsEnabled = true;
                FirstRunIdentityProgress.IsActive = false;
                FirstRunIdentityProgress.Visibility = Visibility.Collapsed;
            }
            _firstRunIdentityInProgress = false;
        }
    }

    private void ApplyFirstRunIdentityState(DesktopFirstRunIdentityState state)
    {
        FirstRunIdentityStateText.Text = state.State;
        FirstRunIdentityDetailText.Text = state.Detail;

        if (string.IsNullOrWhiteSpace(state.KeyId))
        {
            FirstRunIdentityMetadataText.Text = "Nenhum metadado criptográfico local verificado.";
            return;
        }

        var keySuffix = state.KeyId.Length > 12
            ? state.KeyId[^12..]
            : state.KeyId;
        var prepared = state.PreparedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "desconhecido";
        FirstRunIdentityMetadataText.Text =
            $"Tier: {state.SecurityTier ?? "desconhecido"} • Key ID …{keySuffix} • preparado em {prepared}.";
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
        FirstRunEngineStateText.Text = "Verificando…";
        FirstRunEngineDetailText.Text = "Aguardando resposta autenticada do EngineHost.";

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
            FirstRunEngineStateText.Text = status.State;
            FirstRunEngineDetailText.Text = status.Detail;
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
        var showFirstRun = string.Equals(route, "first-run", StringComparison.Ordinal);
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        FirstRunView.Visibility = showFirstRun ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showFirstRun && !showNew
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!showHome && !showFirstRun && !showNew)
        {
            PlannedSectionTitle.Text = title;
        }
    }
}
