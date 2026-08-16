using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopEngineSession _engineSession = new();
    private readonly DesktopFirstRunService _firstRunService = new();
    private readonly DesktopProjectCatalogService _projectCatalogService = new();
    private readonly DesktopEvidenceExplorerService _evidenceExplorerService = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _engineRefreshInProgress;
    private bool _firstRunIdentityInProgress;
    private bool _projectsRefreshInProgress;
    private bool _evidenceProjectsLoadInProgress;
    private bool _evidenceRefreshInProgress;
    private bool _evidenceVerificationInProgress;
    private Grid? _projectsSurface;
    private TextBlock? _projectsStatusText;
    private Button? _refreshProjectsButton;
    private ListView? _projectsList;
    private Grid? _evidenceSurface;
    private ComboBox? _evidenceProjectInput;
    private ComboBox? _evidenceClassificationInput;
    private TextBlock? _evidenceStatusText;
    private ListView? _evidenceList;
    private TextBlock? _evidenceSelectionDetailText;
    private Button? _verifyEvidenceButton;
    private DesktopEvidenceArtifact? _selectedEvidence;

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
        else if (string.Equals(route, "evidence", StringComparison.Ordinal))
        {
            await LoadEvidenceProjectsAsync();
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

    private async void RefreshEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshEvidenceAsync();
    }

    private async void EvidenceProjectInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_evidenceProjectsLoadInProgress)
        {
            await RefreshEvidenceAsync();
        }
    }

    private async void EvidenceClassificationInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_evidenceProjectsLoadInProgress)
        {
            await RefreshEvidenceAsync();
        }
    }

    private void EvidenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedEvidence = (_evidenceList?.SelectedItem as ListViewItem)?.Tag as DesktopEvidenceArtifact;
        ApplyEvidenceSelection();
    }

    private async void VerifyEvidenceButton_Click(object sender, RoutedEventArgs e)
    {
        await VerifySelectedEvidenceAsync();
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

    private async Task LoadEvidenceProjectsAsync()
    {
        if (_evidenceProjectsLoadInProgress || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        EnsureEvidenceSurface();
        _evidenceProjectsLoadInProgress = true;
        _evidenceStatusText!.Text = "Carregando projetos elegíveis para consulta de evidências…";
        _evidenceProjectInput!.IsEnabled = false;

        try
        {
            var previousProjectId = GetSelectedEvidenceProject()?.Id;
            var projects = await _evidenceExplorerService.ListProjectsAsync(_lifetimeCts.Token);
            if (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            _evidenceProjectInput.Items.Clear();
            foreach (var project in projects)
            {
                _evidenceProjectInput.Items.Add(new ComboBoxItem
                {
                    Content = $"{project.Name} • {project.Status}",
                    Tag = project
                });
            }

            if (projects.Count == 0)
            {
                _evidenceStatusText.Text = "Nenhum projeto local válido está disponível para o Evidence Explorer.";
                _evidenceList!.Items.Clear();
                _selectedEvidence = null;
                ApplyEvidenceSelection();
                return;
            }

            var selectedIndex = 0;
            if (previousProjectId is Guid expected)
            {
                for (var index = 0; index < _evidenceProjectInput.Items.Count; index++)
                {
                    if ((_evidenceProjectInput.Items[index] as ComboBoxItem)?.Tag is DesktopEvidenceProject candidate
                        && candidate.Id == expected)
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }

            _evidenceProjectInput.SelectedIndex = selectedIndex;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown intentionally cancels evidence catalog reads.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _evidenceStatusText.Text = $"A lista de projetos foi rejeitada ({ex.GetType().Name}).";
        }
        finally
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _evidenceProjectInput.IsEnabled = true;
            }
            _evidenceProjectsLoadInProgress = false;
        }

        await RefreshEvidenceAsync();
    }

    private async Task RefreshEvidenceAsync()
    {
        if (_evidenceProjectsLoadInProgress
            || _evidenceRefreshInProgress
            || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        EnsureEvidenceSurface();
        var project = GetSelectedEvidenceProject();
        if (project is null)
        {
            _evidenceStatusText!.Text = "Selecione um projeto antes de consultar o índice.";
            _evidenceList!.Items.Clear();
            _selectedEvidence = null;
            ApplyEvidenceSelection();
            return;
        }

        _evidenceRefreshInProgress = true;
        _evidenceStatusText!.Text = "Lendo índice de evidências…";
        _evidenceProjectInput!.IsEnabled = false;
        _evidenceClassificationInput!.IsEnabled = false;
        _evidenceList!.Items.Clear();
        _selectedEvidence = null;
        ApplyEvidenceSelection();

        try
        {
            var state = await _evidenceExplorerService.LoadProjectAsync(
                project.Id,
                GetSelectedEvidenceClassification(),
                _lifetimeCts.Token);
            if (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            _evidenceStatusText.Text = state.Detail;
            foreach (var artifact in state.Artifacts)
            {
                _evidenceList.Items.Add(BuildEvidenceListItem(artifact));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown intentionally cancels evidence reads.
        }
        finally
        {
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _evidenceProjectInput.IsEnabled = true;
                _evidenceClassificationInput.IsEnabled = true;
            }
            _evidenceRefreshInProgress = false;
        }
    }

    private async Task VerifySelectedEvidenceAsync()
    {
        if (_evidenceVerificationInProgress
            || _selectedEvidence is null
            || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        var project = GetSelectedEvidenceProject();
        if (project is null)
        {
            _evidenceSelectionDetailText!.Text = "Verificação bloqueada: nenhum projeto está selecionado.";
            return;
        }

        _evidenceVerificationInProgress = true;
        _verifyEvidenceButton!.IsEnabled = false;
        _evidenceSelectionDetailText!.Text = "Calculando SHA-256 do artefato no armazenamento local…";

        try
        {
            var state = await _evidenceExplorerService.VerifyAsync(
                project.Id,
                _selectedEvidence,
                _lifetimeCts.Token);
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _evidenceSelectionDetailText.Text = state.Detail;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown intentionally cancels integrity verification.
        }
        finally
        {
            _evidenceVerificationInProgress = false;
            if (!_lifetimeCts.IsCancellationRequested)
            {
                _verifyEvidenceButton.IsEnabled = _selectedEvidence is not null;
            }
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

    private void EnsureEvidenceSurface()
    {
        if (_evidenceSurface is not null)
        {
            return;
        }

        var root = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 1180,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };

        root.Children.Add(new TextBlock
        {
            Text = "EVIDENCE EXPLORER",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "Evidência indexada com integridade verificável",
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock
        {
            Text = "Consulta somente leitura sobre o EvidenceStore canônico. Itens em quarentena exibem metadados, mas não são abertos ou executados por esta superfície.",
            FontSize = 16,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        _evidenceProjectInput = new ComboBox
        {
            Header = "Projeto",
            PlaceholderText = "Selecione um projeto local",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _evidenceProjectInput.SelectionChanged += EvidenceProjectInput_SelectionChanged;
        root.Children.Add(_evidenceProjectInput);

        _evidenceClassificationInput = new ComboBox
        {
            Header = "Classificação",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _evidenceClassificationInput.Items.Add(new ComboBoxItem { Content = "Todas", Tag = "all" });
        _evidenceClassificationInput.Items.Add(new ComboBoxItem { Content = "Sanitizadas", Tag = "sanitized" });
        _evidenceClassificationInput.Items.Add(new ComboBoxItem { Content = "Conhecimento neutro", Tag = "knowledge" });
        _evidenceClassificationInput.Items.Add(new ComboBoxItem { Content = "Quarentena", Tag = "quarantine" });
        _evidenceClassificationInput.SelectedIndex = 0;
        _evidenceClassificationInput.SelectionChanged += EvidenceClassificationInput_SelectionChanged;
        root.Children.Add(_evidenceClassificationInput);

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var refreshButton = new Button
        {
            Content = "Atualizar índice",
            AccessKey = "R"
        };
        refreshButton.Click += RefreshEvidenceButton_Click;
        actionRow.Children.Add(refreshButton);

        _evidenceStatusText = new TextBlock
        {
            Text = "Índice ainda não carregado.",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };
        actionRow.Children.Add(_evidenceStatusText);
        root.Children.Add(actionRow);

        root.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "Quarentena permanece isolada",
            Message = "Selecionar uma entrada de quarentena mostra somente metadados. A ação de integridade calcula SHA-256 sem abrir o conteúdo em outra aplicação."
        });

        _evidenceList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxHeight = 420
        };
        _evidenceList.SelectionChanged += EvidenceList_SelectionChanged;
        root.Children.Add(_evidenceList);

        var detailPanel = new StackPanel
        {
            Spacing = 10
        };
        detailPanel.Children.Add(new TextBlock
        {
            Text = "Evidência selecionada",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        _evidenceSelectionDetailText = new TextBlock
        {
            Text = "Selecione uma evidência para inspecionar metadados e verificar sua integridade.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        };
        detailPanel.Children.Add(_evidenceSelectionDetailText);
        _verifyEvidenceButton = new Button
        {
            Content = "Verificar SHA-256",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _verifyEvidenceButton.Click += VerifyEvidenceButton_Click;
        detailPanel.Children.Add(_verifyEvidenceButton);

        root.Children.Add(new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(12),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AevrixPanelBrush"],
            Child = detailPanel
        });

        _evidenceSurface = new Grid
        {
            Padding = new Thickness(32),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        _evidenceSurface.Children.Add(new ScrollViewer
        {
            Content = root,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        PlannedSectionView.Children.Add(_evidenceSurface);
    }

    private DesktopEvidenceProject? GetSelectedEvidenceProject()
        => (_evidenceProjectInput?.SelectedItem as ComboBoxItem)?.Tag as DesktopEvidenceProject;

    private EvidenceClassification? GetSelectedEvidenceClassification()
    {
        var tag = (_evidenceClassificationInput?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "sanitized" => EvidenceClassification.Sanitized,
            "knowledge" => EvidenceClassification.NeutralKnowledge,
            "quarantine" => EvidenceClassification.Quarantine,
            _ => null
        };
    }

    private void ApplyEvidenceSelection()
    {
        if (_evidenceSelectionDetailText is null || _verifyEvidenceButton is null)
        {
            return;
        }

        if (_selectedEvidence is null)
        {
            _evidenceSelectionDetailText.Text = "Selecione uma evidência para inspecionar metadados e verificar sua integridade.";
            _verifyEvidenceButton.IsEnabled = false;
            return;
        }

        var artifact = _selectedEvidence;
        var quarantineNotice = artifact.IsQuarantine
            ? " • QUARENTENA: conteúdo não será aberto nesta superfície"
            : string.Empty;
        _evidenceSelectionDetailText.Text =
            $"{artifact.EvidenceId} • {artifact.Classification} • {artifact.Kind} • {artifact.MediaType} • {FormatBytes(artifact.SizeBytes)} • captura {artifact.CaptureId} • armazenado {artifact.StoredAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz} • SHA-256 {artifact.Sha256}{quarantineNotice}";
        _verifyEvidenceButton.IsEnabled = !_evidenceVerificationInProgress;
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

    private static ListViewItem BuildEvidenceListItem(DesktopEvidenceArtifact artifact)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(4, 10, 4, 10)
        };
        panel.Children.Add(new TextBlock
        {
            Text = $"{artifact.EvidenceId} • {artifact.Kind}",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{artifact.Classification} • {artifact.MediaType} • {FormatBytes(artifact.SizeBytes)} • captura {artifact.CaptureId}",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Armazenado: {artifact.StoredAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz} • SHA-256 {artifact.Sha256[..Math.Min(16, artifact.Sha256.Length)]}…",
            FontSize = 12,
            Opacity = 0.55,
            TextWrapping = TextWrapping.Wrap
        });

        if (artifact.IsQuarantine)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Quarentena — metadados somente; abertura/execução bloqueada nesta superfície.",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new ListViewItem
        {
            Content = panel,
            Tag = artifact,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
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
        var showEvidence = string.Equals(route, "evidence", StringComparison.Ordinal);
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        FirstRunView.Visibility = showFirstRun ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showFirstRun && !showNew
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_projectsSurface is not null)
        {
            _projectsSurface.Visibility = Visibility.Collapsed;
        }
        if (_evidenceSurface is not null)
        {
            _evidenceSurface.Visibility = Visibility.Collapsed;
        }

        if (showProjects)
        {
            EnsureProjectsSurface();
            PlannedSectionView.Children[0].Visibility = Visibility.Collapsed;
            _projectsSurface!.Visibility = Visibility.Visible;
            return;
        }

        if (showEvidence)
        {
            EnsureEvidenceSurface();
            PlannedSectionView.Children[0].Visibility = Visibility.Collapsed;
            _evidenceSurface!.Visibility = Visibility.Visible;
            return;
        }

        if (!showHome && !showFirstRun && !showNew)
        {
            PlannedSectionView.Children[0].Visibility = Visibility.Visible;
            PlannedSectionTitle.Text = title;
        }
    }
}
