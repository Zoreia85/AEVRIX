using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopEngineSession _engineSession = new();
    private readonly DesktopFirstRunService _firstRunService = new();
    private readonly DesktopProjectCatalogService _projectCatalogService = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _engineRefreshInProgress;
    private bool _firstRunIdentityInProgress;
    private bool _projectsRefreshInProgress;
    private Grid? _projectsSurface;
    private TextBlock? _projectsStatusText;
    private Button? _refreshProjectsButton;
    private ListView? _projectsList;

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

    private async void RootNavigation_SelectionChanged(
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

        if (string.Equals(route, "projects", StringComparison.Ordinal))
        {
            await RefreshProjectsAsync();
        }
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

    private async void RefreshProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshProjectsAsync();
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

    private async Task RefreshProjectsAsync()
    {
        if (_projectsRefreshInProgress || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        EnsureProjectsSurface();
        _projectsRefreshInProgress = true;
        _refreshProjectsButton!.IsEnabled = false;
        _projectsStatusText!.Text = "Lendo o repositório local canônico…";
        _projectsList!.Items.Clear();

        try
        {
            var state = await _projectCatalogService.ListAsync(_lifetimeCts.Token);
            if (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            _projectsStatusText.Text = state.Detail;
            foreach (var project in state.Projects)
            {
                _projectsList.Items.Add(BuildProjectListItem(project));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown intentionally cancels catalog reads.
        }
        finally
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _refreshProjectsButton.IsEnabled = true;
            }
            _projectsRefreshInProgress = false;
        }
    }

    private void EnsureProjectsSurface()
    {
        if (_projectsSurface is not null)
        {
            return;
        }

        var root = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        root.Children.Add(new TextBlock
        {
            Text = "PROJETOS",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "Projetos locais desta estação",
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock
        {
            Text = "Leitura direta do ProjectRepository do Core. Esta superfície não cria projetos nem altera autorização, política de navegador ou estado de missão.",
            Opacity = 0.72,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap
        });

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        _refreshProjectsButton = new Button
        {
            Content = "Atualizar catálogo",
            AccessKey = "R"
        };
        _refreshProjectsButton.Click += RefreshProjectsButton_Click;
        actionRow.Children.Add(_refreshProjectsButton);

        _projectsStatusText = new TextBlock
        {
            Text = "Catálogo ainda não carregado.",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };
        actionRow.Children.Add(_projectsStatusText);
        root.Children.Add(actionRow);

        _projectsList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(_projectsList);

        var readOnlyNotice = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Catálogo somente leitura",
            Message = "Abrir, alterar ou executar um projeto será conectado apenas quando os gates de autorização e workspace estiverem integrados."
        };
        root.Children.Add(readOnlyNotice);

        _projectsSurface = new Grid
        {
            Padding = new Thickness(32),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        _projectsSurface.Children.Add(root);
        PlannedSectionView.Children.Add(_projectsSurface);
    }

    private static ListViewItem BuildProjectListItem(DesktopProjectSummary project)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(4, 10, 4, 10)
        };
        panel.Children.Add(new TextBlock
        {
            Text = project.Name,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{project.Domain} • {project.Status} • alvo: {project.TargetId}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Atividade: {project.EffectiveActivityAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz} • sanitizado: {FormatBytes(project.SanitizedBytes)} • quarentena: {FormatBytes(project.QuarantineBytes)}",
            FontSize = 12,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap
        });

        if (project.RequiresAttention)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Atenção local: este projeto possui quarentena, bloqueio ou falha registrada. Nenhum estado saudável é inferido.",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new ListViewItem
        {
            Content = panel,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var value = bytes / 1024d;
        if (value < 1024)
        {
            return $"{value:0.##} KiB";
        }

        value /= 1024d;
        if (value < 1024)
        {
            return $"{value:0.##} MiB";
        }

        return $"{value / 1024d:0.##} GiB";
    }

    private void ShowSection(string route, string title)
    {
        var showHome = string.Equals(route, "home", StringComparison.Ordinal);
        var showFirstRun = string.Equals(route, "first-run", StringComparison.Ordinal);
        var showProjects = string.Equals(route, "projects", StringComparison.Ordinal);
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        FirstRunView.Visibility = showFirstRun ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showFirstRun && !showNew
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showProjects)
        {
            EnsureProjectsSurface();
            if (PlannedSectionView.Children.Count > 0)
            {
                PlannedSectionView.Children[0].Visibility = Visibility.Collapsed;
            }
            _projectsSurface!.Visibility = Visibility.Visible;
            return;
        }

        if (_projectsSurface is not null)
        {
            _projectsSurface.Visibility = Visibility.Collapsed;
        }

        if (!showHome && !showFirstRun && !showNew)
        {
            if (PlannedSectionView.Children.Count > 0)
            {
                PlannedSectionView.Children[0].Visibility = Visibility.Visible;
            }
            PlannedSectionTitle.Text = title;
        }
    }
}
