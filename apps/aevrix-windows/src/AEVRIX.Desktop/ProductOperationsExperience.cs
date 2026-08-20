using System.Diagnostics;
using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AEVRIX.Desktop;

internal sealed class ProductOperationsExperience
{
    private readonly MainWindow _window;
    private readonly NavigationView _navigation;
    private readonly Grid _contentHost;
    private readonly InvestigationRegistryStore _registry = InvestigationRegistryStore.ForCurrentUser();
    private readonly GitHubDesktopConnectionService _gitHub = new();
    private readonly LocalCapacityRecommendation _capacity = LocalCapacityRecommendation.ForCurrentProcess();

    private readonly ScrollViewer _homeView;
    private readonly ScrollViewer _newInvestigationView;
    private readonly ScrollViewer _missionDashboardView;
    private readonly ScrollViewer _integrationsView;
    private readonly ScrollViewer _whiteLabelView;

    private readonly FrameworkElement? _builtinHome;
    private readonly FrameworkElement? _builtinNew;
    private readonly FrameworkElement? _builtinMission;
    private readonly FrameworkElement? _builtinPlanned;

    private readonly ComboBox _targetKindInput = new();
    private readonly ComboBox _strategyInput = new();
    private readonly ComboBox _authorizationInput = new();
    private readonly TextBox _workspaceInput = new();
    private readonly TextBox _targetInput = new();
    private readonly TextBox _artifactInput = new();
    private readonly TextBox _goalsInput = new();
    private readonly ComboBox _sensitivityInput = new();
    private readonly InfoBar _investigationNotice = new();

    private readonly TextBlock _homeGitHubStatus = new();
    private readonly TextBlock _homeCanonicalSha = new();
    private readonly TextBlock _homeInvestigationSummary = new();
    private readonly StackPanel _missionList = new();
    private readonly TextBlock _missionSummary = new();

    private readonly TextBox _gitHubClientIdInput = new();
    private readonly TextBlock _gitHubStatus = new();
    private readonly TextBlock _gitHubIdentity = new();
    private readonly TextBlock _gitHubSha = new();
    private readonly InfoBar _gitHubNotice = new();

    private readonly TextBox _whiteLabelBlueprintInput = new();
    private readonly TextBox _whiteLabelProductNameInput = new();
    private readonly TextBox _whiteLabelBrandInput = new();
    private readonly TextBox _whiteLabelGoalsInput = new();
    private readonly InfoBar _whiteLabelNotice = new();

    private ProductOperationsExperience(MainWindow window)
    {
        _window = window;
        _navigation = FindDescendant<NavigationView>(window.Content)
            ?? throw new InvalidOperationException("AEVRIX NavigationView was not found.");
        _contentHost = _navigation.Content as Grid
            ?? throw new InvalidOperationException("AEVRIX navigation content host is not a Grid.");

        _builtinHome = FindDescendant<FrameworkElement>(window.Content, "CommandCenterView");
        _builtinNew = FindDescendant<FrameworkElement>(window.Content, "NewInvestigationView");
        _builtinMission = FindDescendant<FrameworkElement>(window.Content, "MissionControlView");
        _builtinPlanned = FindDescendant<FrameworkElement>(window.Content, "PlannedSectionView");

        _homeView = BuildHomeView();
        _newInvestigationView = BuildNewInvestigationView();
        _missionDashboardView = BuildMissionDashboardView();
        _integrationsView = BuildIntegrationsView();
        _whiteLabelView = BuildWhiteLabelView();

        _contentHost.Children.Add(_homeView);
        _contentHost.Children.Add(_newInvestigationView);
        _contentHost.Children.Add(_missionDashboardView);
        _contentHost.Children.Add(_integrationsView);
        _contentHost.Children.Add(_whiteLabelView);

        AddWhiteLabelNavigationItem();
        RenameOperationalNavigation();
        _navigation.SelectionChanged += Navigation_SelectionChanged;

        InitializeDefaults();
        ShowProductRoute("home");
        RefreshRegistryViews();
        _ = RefreshGitHubAsync();
    }

    public static void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _ = new ProductOperationsExperience(window);
    }

    private void InitializeDefaults()
    {
        _strategyInput.SelectedIndex = 0;
        _sensitivityInput.SelectedIndex = 0;
        _workspaceInput.Text = $"investigacao-{DateTime.Now:yyyyMMdd-HHmm}";
        _goalsInput.Text = "Mapear arquitetura, comportamento, interfaces, dependências e evidências; produzir Blueprint auditável e identificar lacunas para validação.";
        _gitHubClientIdInput.Text = LoadGitHubClientId();
        _whiteLabelGoalsInput.Text = "Reconstruir uma implementação clean-room funcionalmente equivalente, com identidade visual e marca substituíveis, preservando somente comportamentos comprovados no Blueprint.";
    }

    private ScrollViewer BuildHomeView()
    {
        var root = CreatePageStack();
        root.Children.Add(CreateEyebrow("CENTRO OPERACIONAL"));
        root.Children.Add(CreateTitle("O AEVRIX trabalha por você — e mostra o que está acontecendo"));
        root.Children.Add(CreateBody("Comece pelo tipo de trabalho. O sistema prepara padrões seguros automaticamente, mede a capacidade deste computador e acompanha várias investigações sem transformar estados não comprovados em sucesso."));

        var quickGrid = new Grid { ColumnSpacing = 14 };
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition());
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition());
        quickGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var capacityPanel = new StackPanel { Spacing = 6 };
        capacityPanel.Children.Add(CreateLabel("CAPACIDADE LOCAL"));
        capacityPanel.Children.Add(new TextBlock
        {
            Text = $"até {_capacity.RecommendedConcurrentInvestigations} em paralelo",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        capacityPanel.Children.Add(CreateMuted(_capacity.Rationale));
        quickGrid.Children.Add(CreateCard(capacityPanel, 0));

        var githubPanel = new StackPanel { Spacing = 6 };
        githubPanel.Children.Add(CreateLabel("GITHUB"));
        _homeGitHubStatus.Text = "Verificando...";
        _homeGitHubStatus.FontSize = 22;
        githubPanel.Children.Add(_homeGitHubStatus);
        _homeCanonicalSha.Text = "main: aguardando prova";
        githubPanel.Children.Add(CreateMutedContainer(_homeCanonicalSha));
        quickGrid.Children.Add(CreateCard(githubPanel, 1));

        var workPanel = new StackPanel { Spacing = 6 };
        workPanel.Children.Add(CreateLabel("TRABALHO"));
        _homeInvestigationSummary.FontSize = 22;
        _homeInvestigationSummary.Text = "Nenhuma investigação registrada";
        workPanel.Children.Add(_homeInvestigationSummary);
        workPanel.Children.Add(CreateMuted("Ativas, em fila, pausadas, bloqueadas e concluídas aparecem separadamente no Painel de operações."));
        quickGrid.Children.Add(CreateCard(workPanel, 2));
        root.Children.Add(quickGrid);

        root.Children.Add(CreateSectionTitle("Escolha o que deseja fazer"));
        var strategyGrid = new Grid { ColumnSpacing = 14, RowSpacing = 14 };
        strategyGrid.ColumnDefinitions.Add(new ColumnDefinition());
        strategyGrid.ColumnDefinitions.Add(new ColumnDefinition());
        strategyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        strategyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddStrategyCard(strategyGrid, 0, 0,
            "Investigar",
            "Coleta, análise estática, evidências, correlação e Blueprint auditável.",
            "Começar investigação",
            InvestigationStrategy.Investigate);
        AddStrategyCard(strategyGrid, 0, 1,
            "Investigar + Emular",
            "Para aplicativos e softwares executáveis: investigar e também instalar/executar testes em ambiente governado.",
            "Investigar e emular",
            InvestigationStrategy.InvestigateAndEmulate);
        AddStrategyCard(strategyGrid, 1, 0,
            "Investigar + Criar em paralelo",
            "Agentes de investigação continuam coletando enquanto agentes de reconstrução implementam somente pacotes já comprovados.",
            "Trabalhar em paralelo",
            InvestigationStrategy.InvestigateAndBuildParallel);
        AddStrategyCard(strategyGrid, 1, 1,
            "Reconstruir / Whitelabel",
            "Use um Blueprint suficientemente investigado para produzir uma versão clean-room com nome, cores e marca substituíveis.",
            "Abrir Whitelabel",
            InvestigationStrategy.ReconstructWhiteLabel);
        root.Children.Add(strategyGrid);

        var info = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Progresso real, não cronômetro",
            Message = "A porcentagem será calculada pelo peso de etapas e gates realmente concluídos. ETA só será exibido quando houver amostras de execução suficientes."
        };
        root.Children.Add(info);

        return Wrap(root);
    }

    private ScrollViewer BuildNewInvestigationView()
    {
        var root = CreatePageStack();
        root.Children.Add(CreateEyebrow("NOVA INVESTIGAÇÃO"));
        root.Children.Add(CreateTitle("Defina o alvo; o AEVRIX prepara o restante"));
        root.Children.Add(CreateBody("Escolha se o alvo é um aplicativo/software instalado, app móvel, sistema online/site, API, repositório ou conjunto de arquivos. Para executáveis, anexe instalador/pacote e os artefatos autorizados necessários para a análise."));

        var form = new StackPanel { Spacing = 16 };
        form.Children.Add(CreateSectionTitle("1. Tipo de alvo e estratégia"));

        _targetKindInput.Header = "O que será investigado?";
        _targetKindInput.PlaceholderText = "Escolha o tipo do alvo";
        AddComboItem(_targetKindInput, "Aplicativo / software Windows ou desktop", InvestigationTargetKind.DesktopApplication);
        AddComboItem(_targetKindInput, "Aplicativo móvel (Android / pacote autorizado)", InvestigationTargetKind.MobileApplication);
        AddComboItem(_targetKindInput, "Sistema online / site", InvestigationTargetKind.WebSystem);
        AddComboItem(_targetKindInput, "API / serviço", InvestigationTargetKind.ApiService);
        AddComboItem(_targetKindInput, "Repositório / código fornecido com autorização", InvestigationTargetKind.Repository);
        AddComboItem(_targetKindInput, "Arquivos, documentos ou evidências", InvestigationTargetKind.EvidenceFiles);
        AddComboItem(_targetKindInput, "Outro alvo autorizado", InvestigationTargetKind.Other);
        _targetKindInput.SelectionChanged += (_, _) => RefreshArtifactGuidance();
        form.Children.Add(_targetKindInput);

        _strategyInput.Header = "Como o AEVRIX deve trabalhar?";
        AddComboItem(_strategyInput, "Investigar e produzir Blueprint", InvestigationStrategy.Investigate);
        AddComboItem(_strategyInput, "Investigar + Emular / executar testes", InvestigationStrategy.InvestigateAndEmulate);
        AddComboItem(_strategyInput, "Investigar + Criar em paralelo", InvestigationStrategy.InvestigateAndBuildParallel);
        AddComboItem(_strategyInput, "Reconstruir / Whitelabel a partir de Blueprint", InvestigationStrategy.ReconstructWhiteLabel);
        form.Children.Add(_strategyInput);

        form.Children.Add(CreateSectionTitle("2. Escopo autorizado"));
        _authorizationInput.Header = "Qual é sua autorização sobre este alvo?";
        _authorizationInput.PlaceholderText = "Confirmação obrigatória";
        AddComboItem(_authorizationInput, "É um sistema próprio", "owned");
        AddComboItem(_authorizationInput, "Tenho autorização explícita", "authorized");
        AddComboItem(_authorizationInput, "Análise de terceiro sob método clean-room autorizado", "clean-room");
        form.Children.Add(_authorizationInput);

        _workspaceInput.Header = "Nome do projeto / workspace";
        _workspaceInput.PlaceholderText = "Gerado automaticamente — altere se quiser";
        form.Children.Add(_workspaceInput);

        _targetInput.Header = "Alvo principal";
        _targetInput.PlaceholderText = "Nome do aplicativo, URL HTTPS, API, repositório ou descrição do conjunto de evidências";
        form.Children.Add(_targetInput);

        _artifactInput.Header = "Anexos / artefatos locais";
        _artifactInput.PlaceholderText = "Um caminho por linha: instalador, .exe, .msi, .apk/.aab, dependências, configurações, amostras ou documentação autorizada";
        _artifactInput.AcceptsReturn = true;
        _artifactInput.MinHeight = 90;
        _artifactInput.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(_artifactInput);

        _goalsInput.Header = "Objetivo";
        _goalsInput.AcceptsReturn = true;
        _goalsInput.MinHeight = 100;
        _goalsInput.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(_goalsInput);

        _sensitivityInput.Header = "Sensibilidade dos dados";
        AddComboItem(_sensitivityInput, "Padrão (recomendado)", "standard");
        AddComboItem(_sensitivityInput, "Confidencial", "confidential");
        AddComboItem(_sensitivityInput, "Dados pessoais / restritos", "restricted");
        form.Children.Add(_sensitivityInput);

        var capacity = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Concorrência governada",
            Message = $"Este PC recomenda até {_capacity.RecommendedConcurrentInvestigations} investigações simultâneas neste momento. O orquestrador poderá reduzir o paralelismo se CPU ou memória entrarem sob pressão."
        };
        form.Children.Add(capacity);

        var button = new Button
        {
            Content = "Preparar investigação",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += PrepareInvestigation_Click;
        form.Children.Add(button);

        _investigationNotice.IsOpen = false;
        _investigationNotice.IsClosable = true;
        form.Children.Add(_investigationNotice);
        root.Children.Add(CreateCard(form));

        return Wrap(root);
    }

    private ScrollViewer BuildMissionDashboardView()
    {
        var root = CreatePageStack();
        root.Children.Add(CreateEyebrow("PAINEL DE OPERAÇÕES"));
        root.Children.Add(CreateTitle("Investigações, conexões e gargalos em um único lugar"));
        root.Children.Add(CreateBody("Cada investigação deve mostrar estado, fase, porcentagem comprovada, ETA quando calculável, última atividade e motivo de bloqueio. O painel não simula execução que ainda não esteja ligada ao orquestrador."));

        _missionSummary.FontSize = 18;
        root.Children.Add(CreateCard(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                CreateLabel("RESUMO"),
                _missionSummary,
                CreateMuted($"Capacidade local recomendada: {_capacity.RecommendedConcurrentInvestigations} investigações simultâneas.")
            }
        }));

        var refresh = new Button { Content = "Atualizar painel", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (_, _) =>
        {
            RefreshRegistryViews();
            _ = RefreshGitHubAsync();
        };
        root.Children.Add(refresh);

        _missionList.Spacing = 12;
        root.Children.Add(_missionList);
        return Wrap(root);
    }

    private ScrollViewer BuildIntegrationsView()
    {
        var root = CreatePageStack();
        root.Children.Add(CreateEyebrow("INTEGRAÇÕES"));
        root.Children.Add(CreateTitle("GitHub precisa ser uma conexão operacional do AEVRIX"));
        root.Children.Add(CreateBody("O Desktop usa Device Flow: nenhuma senha do GitHub e nenhum client_secret ficam embutidos. O token autorizado é armazenado no Windows Credential Manager e pode ser revogado a qualquer momento."));

        var statusPanel = new StackPanel { Spacing = 8 };
        statusPanel.Children.Add(CreateLabel("ESTADO DA CONEXÃO"));
        _gitHubStatus.FontSize = 24;
        _gitHubStatus.Text = "Verificando...";
        statusPanel.Children.Add(_gitHubStatus);
        _gitHubIdentity.Text = "Conta: não autenticada";
        statusPanel.Children.Add(_gitHubIdentity);
        _gitHubSha.Text = "main canônico: aguardando prova";
        statusPanel.Children.Add(CreateMutedContainer(_gitHubSha));
        root.Children.Add(CreateCard(statusPanel));

        var connectionPanel = new StackPanel { Spacing = 12 };
        connectionPanel.Children.Add(CreateSectionTitle("Conectar conta GitHub"));
        connectionPanel.Children.Add(CreateBody("É necessário registrar uma GitHub App do AEVRIX com Device Flow habilitado. O Client ID não é segredo e pode ser salvo neste PC."));
        _gitHubClientIdInput.Header = "GitHub App Client ID";
        _gitHubClientIdInput.PlaceholderText = "Cole o Client ID da GitHub App AEVRIX";
        connectionPanel.Children.Add(_gitHubClientIdInput);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var connect = new Button { Content = "Conectar GitHub" };
        connect.Click += ConnectGitHub_Click;
        var disconnect = new Button { Content = "Desconectar" };
        disconnect.Click += DisconnectGitHub_Click;
        var refresh = new Button { Content = "Testar conexão" };
        refresh.Click += (_, _) => _ = RefreshGitHubAsync();
        buttons.Children.Add(connect);
        buttons.Children.Add(refresh);
        buttons.Children.Add(disconnect);
        connectionPanel.Children.Add(buttons);

        _gitHubNotice.IsOpen = false;
        _gitHubNotice.IsClosable = true;
        connectionPanel.Children.Add(_gitHubNotice);
        root.Children.Add(CreateCard(connectionPanel));

        root.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Permissões mínimas",
            Message = "Leitura de repositório/Actions é separada de escrita. Disparar ou cancelar workflows só será habilitado quando a GitHub App possuir a permissão correspondente e o usuário tiver autorizado a operação."
        });
        return Wrap(root);
    }

    private ScrollViewer BuildWhiteLabelView()
    {
        var root = CreatePageStack();
        root.Children.Add(CreateEyebrow("RECONSTRUÇÃO / WHITELABEL"));
        root.Children.Add(CreateTitle("Transforme uma investigação comprovada em um produto novo e configurável"));
        root.Children.Add(CreateBody("Esta etapa trabalha sobre Blueprint clean-room aprovado. Comportamentos podem ser reconstruídos; código proprietário, segredos, logotipos, nomes e ativos protegidos do original não são copiados."));

        var form = new StackPanel { Spacing = 14 };
        _whiteLabelBlueprintInput.Header = "Blueprint / investigação de origem";
        _whiteLabelBlueprintInput.PlaceholderText = "Informe o workspace ou identificador da investigação concluída";
        form.Children.Add(_whiteLabelBlueprintInput);

        _whiteLabelProductNameInput.Header = "Nome do novo produto";
        _whiteLabelProductNameInput.PlaceholderText = "Nome substituível / whitelabel";
        form.Children.Add(_whiteLabelProductNameInput);

        _whiteLabelBrandInput.Header = "Marca inicial";
        _whiteLabelBrandInput.PlaceholderText = "Opcional — poderá ser trocada depois";
        form.Children.Add(_whiteLabelBrandInput);

        _whiteLabelGoalsInput.Header = "Objetivo de reconstrução";
        _whiteLabelGoalsInput.AcceptsReturn = true;
        _whiteLabelGoalsInput.MinHeight = 100;
        _whiteLabelGoalsInput.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(_whiteLabelGoalsInput);

        var prepare = new Button { Content = "Preparar reconstrução clean-room", HorizontalAlignment = HorizontalAlignment.Left };
        prepare.Click += PrepareWhiteLabel_Click;
        form.Children.Add(prepare);
        _whiteLabelNotice.IsOpen = false;
        _whiteLabelNotice.IsClosable = true;
        form.Children.Add(_whiteLabelNotice);
        root.Children.Add(CreateCard(form));
        return Wrap(root);
    }

    private void AddStrategyCard(
        Grid grid,
        int row,
        int column,
        string title,
        string detail,
        string action,
        InvestigationStrategy strategy)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(CreateMuted(detail));
        var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => NavigateToStrategy(strategy);
        panel.Children.Add(button);
        var card = CreateCard(panel);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private void NavigateToStrategy(InvestigationStrategy strategy)
    {
        if (strategy is InvestigationStrategy.ReconstructWhiteLabel)
        {
            var item = FindNavigationItem("reconstruct");
            if (item is not null)
            {
                _navigation.SelectedItem = item;
            }
            ShowProductRoute("reconstruct");
            return;
        }

        var newItem = FindNavigationItem("new");
        if (newItem is not null)
        {
            _navigation.SelectedItem = newItem;
        }
        SelectComboByTag(_strategyInput, strategy);
        ShowProductRoute("new");
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var route = args.SelectedItemContainer is NavigationViewItem item
            ? item.Tag?.ToString()
            : null;

        if (route is "home" or "new" or "mission" or "integrations" or "reconstruct")
        {
            ShowProductRoute(route);
            if (route is "mission")
            {
                RefreshRegistryViews();
                _ = RefreshGitHubAsync();
            }
            else if (route is "integrations")
            {
                _ = RefreshGitHubAsync();
            }
            return;
        }

        HideProductViews();
    }

    private void ShowProductRoute(string route)
    {
        HideProductViews();
        switch (route)
        {
            case "home":
                Hide(_builtinHome);
                _homeView.Visibility = Visibility.Visible;
                break;
            case "new":
                Hide(_builtinNew);
                _newInvestigationView.Visibility = Visibility.Visible;
                break;
            case "mission":
                Hide(_builtinMission);
                _missionDashboardView.Visibility = Visibility.Visible;
                break;
            case "integrations":
                Hide(_builtinPlanned);
                _integrationsView.Visibility = Visibility.Visible;
                break;
            case "reconstruct":
                Hide(_builtinPlanned);
                _whiteLabelView.Visibility = Visibility.Visible;
                break;
        }
    }

    private void HideProductViews()
    {
        _homeView.Visibility = Visibility.Collapsed;
        _newInvestigationView.Visibility = Visibility.Collapsed;
        _missionDashboardView.Visibility = Visibility.Collapsed;
        _integrationsView.Visibility = Visibility.Collapsed;
        _whiteLabelView.Visibility = Visibility.Collapsed;
    }

    private void PrepareInvestigation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_targetKindInput.SelectedItem is not ComboBoxItem targetItem || targetItem.Tag is not InvestigationTargetKind targetKind)
            {
                throw new InvalidOperationException("Escolha o tipo do alvo.");
            }
            if (_strategyInput.SelectedItem is not ComboBoxItem strategyItem || strategyItem.Tag is not InvestigationStrategy strategy)
            {
                throw new InvalidOperationException("Escolha a estratégia de trabalho.");
            }
            if (_authorizationInput.SelectedItem is not ComboBoxItem authorizationItem || authorizationItem.Tag is not string authorization)
            {
                throw new InvalidOperationException("Confirme a classe de autorização.");
            }
            if (_sensitivityInput.SelectedItem is not ComboBoxItem sensitivityItem || sensitivityItem.Tag is not string sensitivity)
            {
                throw new InvalidOperationException("Escolha a sensibilidade dos dados.");
            }

            var artifacts = ParseArtifacts(_artifactInput.Text);
            var draft = InvestigationDraft.Create(
                _workspaceInput.Text,
                targetKind,
                strategy,
                authorization,
                _targetInput.Text,
                _goalsInput.Text,
                sensitivity,
                artifacts);

            var entry = _registry.AddDraft(draft);
            _investigationNotice.Severity = InfoBarSeverity.Success;
            _investigationNotice.Title = "Investigação preparada";
            _investigationNotice.Message = $"{entry.Workspace} foi registrada. A execução continua bloqueada até o motor de políticas/orquestrador assumir a missão; nenhum progresso foi simulado.";
            _investigationNotice.IsOpen = true;
            RefreshRegistryViews();
            _workspaceInput.Text = $"investigacao-{DateTime.Now:yyyyMMdd-HHmm}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _investigationNotice.Severity = InfoBarSeverity.Error;
            _investigationNotice.Title = "Revise os dados";
            _investigationNotice.Message = ex.Message;
            _investigationNotice.IsOpen = true;
        }
    }

    private void PrepareWhiteLabel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_whiteLabelBlueprintInput.Text))
            {
                throw new InvalidOperationException("Informe a investigação ou Blueprint de origem.");
            }
            if (string.IsNullOrWhiteSpace(_whiteLabelProductNameInput.Text))
            {
                throw new InvalidOperationException("Informe o nome inicial do novo produto.");
            }

            var draft = InvestigationDraft.Create(
                $"whitelabel-{DateTime.Now:yyyyMMdd-HHmm}",
                InvestigationTargetKind.Other,
                InvestigationStrategy.ReconstructWhiteLabel,
                "clean-room",
                _whiteLabelBlueprintInput.Text,
                _whiteLabelGoalsInput.Text,
                "standard");
            _registry.AddDraft(draft);
            _whiteLabelNotice.Severity = InfoBarSeverity.Success;
            _whiteLabelNotice.Title = "Reconstrução preparada";
            _whiteLabelNotice.Message = $"'{_whiteLabelProductNameInput.Text.Trim()}' foi registrada como reconstrução clean-room. Implementação só poderá avançar com evidência/Blueprint suficiente.";
            _whiteLabelNotice.IsOpen = true;
            RefreshRegistryViews();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _whiteLabelNotice.Severity = InfoBarSeverity.Error;
            _whiteLabelNotice.Title = "Não foi possível preparar";
            _whiteLabelNotice.Message = ex.Message;
            _whiteLabelNotice.IsOpen = true;
        }
    }

    private async void ConnectGitHub_Click(object sender, RoutedEventArgs e)
    {
        var clientId = _gitHubClientIdInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _gitHubNotice.Severity = InfoBarSeverity.Warning;
            _gitHubNotice.Title = "Client ID necessário";
            _gitHubNotice.Message = "Registre a GitHub App AEVRIX com Device Flow habilitado e cole o Client ID aqui.";
            _gitHubNotice.IsOpen = true;
            return;
        }

        try
        {
            SaveGitHubClientId(clientId);
            _gitHubNotice.Severity = InfoBarSeverity.Informational;
            _gitHubNotice.Title = "Solicitando código ao GitHub";
            _gitHubNotice.Message = "Aguarde...";
            _gitHubNotice.IsOpen = true;

            var code = await _gitHub.RequestDeviceCodeAsync(clientId);
            _gitHubNotice.Title = $"Código GitHub: {code.UserCode}";
            _gitHubNotice.Message = "O navegador será aberto. Confirme o código no GitHub; o AEVRIX continuará automaticamente após a autorização.";
            Process.Start(new ProcessStartInfo
            {
                FileName = code.VerificationUri.ToString(),
                UseShellExecute = true
            });

            await _gitHub.CompleteDeviceFlowAsync(clientId, code);
            _gitHubNotice.Severity = InfoBarSeverity.Success;
            _gitHubNotice.Title = "GitHub conectado";
            _gitHubNotice.Message = "O token foi guardado no Windows Credential Manager. O AEVRIX não grava o segredo em project.json ou logs.";
            await RefreshGitHubAsync();
        }
        catch (Exception ex)
        {
            _gitHubNotice.Severity = InfoBarSeverity.Error;
            _gitHubNotice.Title = "Conexão GitHub falhou";
            _gitHubNotice.Message = ex.Message;
            _gitHubNotice.IsOpen = true;
        }
    }

    private async void DisconnectGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _gitHub.Disconnect();
            _gitHubNotice.Severity = InfoBarSeverity.Success;
            _gitHubNotice.Title = "GitHub desconectado";
            _gitHubNotice.Message = "O token local foi removido do Windows Credential Manager.";
            _gitHubNotice.IsOpen = true;
        }
        catch (Exception ex)
        {
            _gitHubNotice.Severity = InfoBarSeverity.Error;
            _gitHubNotice.Title = "Falha ao remover credencial";
            _gitHubNotice.Message = ex.Message;
            _gitHubNotice.IsOpen = true;
        }
        await RefreshGitHubAsync();
    }

    private async Task RefreshGitHubAsync()
    {
        var snapshot = await _gitHub.ProbeAsync();
        _homeGitHubStatus.Text = snapshot.Authenticated
            ? "Autenticado"
            : snapshot.ApiReachable ? "Leitura pública" : "Indisponível";
        _homeCanonicalSha.Text = snapshot.CanonicalSha is { Length: >= 12 }
            ? $"main: {snapshot.CanonicalSha[..12]}…"
            : "main: não comprovado";

        _gitHubStatus.Text = snapshot.Authenticated
            ? "CONECTADO"
            : snapshot.ApiReachable ? "PARCIAL — SEM AUTENTICAÇÃO" : "BLOQUEADO";
        _gitHubIdentity.Text = snapshot.Authenticated
            ? $"Conta: {snapshot.Login}"
            : "Conta: não autenticada";
        _gitHubSha.Text = snapshot.CanonicalSha is { Length: > 0 }
            ? $"main canônico: {snapshot.CanonicalSha}"
            : "main canônico: não comprovado";
    }

    private void RefreshRegistryViews()
    {
        var entries = _registry.Load();
        var running = entries.Count(entry => entry.State is InvestigationRunState.Running);
        var queued = entries.Count(entry => entry.State is InvestigationRunState.Queued or InvestigationRunState.Ready);
        var paused = entries.Count(entry => entry.State is InvestigationRunState.Paused);
        var blocked = entries.Count(entry => entry.State is InvestigationRunState.Blocked or InvestigationRunState.Draft);
        var failed = entries.Count(entry => entry.State is InvestigationRunState.Failed);
        var completed = entries.Count(entry => entry.State is InvestigationRunState.Completed);

        _homeInvestigationSummary.Text = entries.Count == 0
            ? "Nenhuma investigação registrada"
            : $"{running} executando • {queued} em fila • {blocked} aguardando";
        _missionSummary.Text = $"{running} executando • {queued} em fila • {paused} pausadas • {blocked} bloqueadas/aguardando • {failed} com falha • {completed} concluídas";

        _missionList.Children.Clear();
        if (entries.Count == 0)
        {
            _missionList.Children.Add(CreateCard(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    CreateSectionTitle("Nenhuma investigação ainda"),
                    CreateMuted("Use Nova investigação. Quando o orquestrador estiver conectado, este painel passará a mostrar progresso e ETA de cada missão.")
                }
            }));
            return;
        }

        foreach (var entry in entries.Take(30))
        {
            var panel = new StackPanel { Spacing = 7 };
            panel.Children.Add(new TextBlock
            {
                Text = entry.Workspace,
                FontSize = 21,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"{Describe(entry.TargetKind)} • {Describe(entry.Strategy)} • {Describe(entry.State)}",
                Opacity = 0.72
            });
            panel.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = entry.PercentComplete,
                Height = 8
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"{entry.PercentComplete:F1}% • fase: {Describe(entry.CurrentPhase)} • ETA: {entry.EstimatedRemaining ?? "indisponível até haver amostras suficientes"}",
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(entry.Blocker))
            {
                panel.Children.Add(CreateMuted($"Próximo bloqueio: {entry.Blocker}"));
            }
            panel.Children.Add(CreateMuted($"Última atividade: {entry.LastActivityAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}"));
            _missionList.Children.Add(CreateCard(panel));
        }
    }

    private void RefreshArtifactGuidance()
    {
        if (_targetKindInput.SelectedItem is not ComboBoxItem item || item.Tag is not InvestigationTargetKind kind)
        {
            return;
        }

        _artifactInput.Header = InvestigationDraft.RequiresExecutableArtifacts(kind)
            ? "Anexos / artefatos locais — obrigatório para aplicativo/software"
            : "Anexos / artefatos locais — opcional quando aplicável";
    }

    private void AddWhiteLabelNavigationItem()
    {
        if (FindNavigationItem("reconstruct") is not null)
        {
            return;
        }

        var item = new NavigationViewItem
        {
            Content = "Reconstrução / Whitelabel",
            Tag = "reconstruct"
        };

        var newIndex = -1;
        for (var index = 0; index < _navigation.MenuItems.Count; index++)
        {
            if (_navigation.MenuItems[index] is NavigationViewItem existing &&
                string.Equals(existing.Tag?.ToString(), "new", StringComparison.Ordinal))
            {
                newIndex = index;
                break;
            }
        }

        if (newIndex >= 0)
        {
            _navigation.MenuItems.Insert(newIndex + 1, item);
        }
        else
        {
            _navigation.MenuItems.Add(item);
        }
    }

    private void RenameOperationalNavigation()
    {
        var mission = FindNavigationItem("mission");
        if (mission is not null)
        {
            mission.Content = "Painel de operações";
        }
    }

    private NavigationViewItem? FindNavigationItem(string tag)
    {
        foreach (var entry in _navigation.MenuItems)
        {
            if (entry is NavigationViewItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                return item;
            }
        }
        return null;
    }

    private static IReadOnlyList<InvestigationInputArtifact> ParseArtifacts(string text)
        => (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new InvestigationInputArtifact(Path.GetFileName(path), path))
            .ToArray();

    private static void AddComboItem(ComboBox combo, string label, object tag)
        => combo.Items.Add(new ComboBoxItem { Content = label, Tag = tag });

    private static void SelectComboByTag(ComboBox combo, object tag)
    {
        foreach (var entry in combo.Items)
        {
            if (entry is ComboBoxItem item && Equals(item.Tag, tag))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static string Describe(InvestigationTargetKind value) => value switch
    {
        InvestigationTargetKind.DesktopApplication => "Software desktop",
        InvestigationTargetKind.MobileApplication => "App móvel",
        InvestigationTargetKind.WebSystem => "Sistema online",
        InvestigationTargetKind.ApiService => "API/serviço",
        InvestigationTargetKind.Repository => "Repositório",
        InvestigationTargetKind.EvidenceFiles => "Arquivos/evidências",
        _ => "Outro alvo"
    };

    private static string Describe(InvestigationStrategy value) => value switch
    {
        InvestigationStrategy.Investigate => "Investigar",
        InvestigationStrategy.InvestigateAndEmulate => "Investigar + Emular",
        InvestigationStrategy.InvestigateAndBuildParallel => "Investigar + Criar",
        InvestigationStrategy.ReconstructWhiteLabel => "Whitelabel",
        _ => value.ToString()
    };

    private static string Describe(InvestigationRunState value) => value switch
    {
        InvestigationRunState.Draft => "Aguardando orquestrador",
        InvestigationRunState.Ready => "Pronta",
        InvestigationRunState.Queued => "Em fila",
        InvestigationRunState.Running => "Executando",
        InvestigationRunState.Paused => "Pausada",
        InvestigationRunState.Blocked => "Bloqueada",
        InvestigationRunState.Failed => "Falha",
        InvestigationRunState.Completed => "Concluída",
        InvestigationRunState.Cancelled => "Cancelada",
        _ => value.ToString()
    };

    private static string Describe(InvestigationPhase value) => value switch
    {
        InvestigationPhase.IntakeAndAuthorization => "entrada e autorização",
        InvestigationPhase.Acquisition => "aquisição",
        InvestigationPhase.StaticAnalysis => "análise estática",
        InvestigationPhase.DynamicObservation => "emulação / observação dinâmica",
        InvestigationPhase.EvidenceCorrelation => "correlação de evidências",
        InvestigationPhase.BlueprintSynthesis => "síntese do Blueprint",
        InvestigationPhase.DifferentialValidation => "validação diferencial",
        InvestigationPhase.Reconstruction => "reconstrução",
        InvestigationPhase.FinalQualityAssurance => "QA final",
        _ => value.ToString()
    };

    private static ScrollViewer Wrap(StackPanel root)
        => new()
        {
            Visibility = Visibility.Collapsed,
            Content = new Grid
            {
                MaxWidth = 1180,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(40, 32, 40, 48),
                Children = { root }
            }
        };

    private static StackPanel CreatePageStack()
        => new() { Spacing = 18 };

    private static TextBlock CreateEyebrow(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ResourceBrush("AevrixAccentBrush")
        };

    private static TextBlock CreateTitle(string text)
        => new()
        {
            Text = text,
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

    private static TextBlock CreateSectionTitle(string text)
        => new()
        {
            Text = text,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

    private static TextBlock CreateBody(string text)
        => new()
        {
            Text = text,
            FontSize = 16,
            Opacity = 0.74,
            TextWrapping = TextWrapping.Wrap
        };

    private static TextBlock CreateMuted(string text)
        => new()
        {
            Text = text,
            Opacity = 0.66,
            TextWrapping = TextWrapping.Wrap
        };

    private static TextBlock CreateLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ResourceBrush("AevrixAccentBrush")
        };

    private static UIElement CreateMutedContainer(TextBlock text)
    {
        text.Opacity = 0.66;
        text.TextWrapping = TextWrapping.Wrap;
        return text;
    }

    private static Border CreateCard(UIElement content, int? column = null)
    {
        var card = new Border
        {
            Background = ResourceBrush("AevrixPanelBrush"),
            BorderBrush = ResourceBrush("AevrixBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20),
            Child = content
        };
        if (column is int value)
        {
            Grid.SetColumn(card, value);
        }
        return card;
    }

    private static Brush ResourceBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static void Hide(FrameworkElement? element)
    {
        if (element is not null)
        {
            element.Visibility = Visibility.Collapsed;
        }
    }

    private static T? FindDescendant<T>(object? root, string? name = null)
        where T : DependencyObject
    {
        if (root is T typed && (name is null || (typed as FrameworkElement)?.Name == name))
        {
            return typed;
        }
        if (root is not DependencyObject dependency)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(dependency);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(dependency, index);
            var found = FindDescendant<T>(child, name);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static string ClientIdPath
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AEVRIX",
                "UserData");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "github-client-id.txt");
        }
    }

    private static string LoadGitHubClientId()
    {
        try
        {
            return File.Exists(ClientIdPath) ? File.ReadAllText(ClientIdPath).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SaveGitHubClientId(string clientId)
        => File.WriteAllText(ClientIdPath, clientId.Trim());
}
