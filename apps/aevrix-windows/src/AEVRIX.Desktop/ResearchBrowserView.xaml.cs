using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace AEVRIX.Desktop;

public sealed partial class ResearchBrowserView : UserControl
{
    private readonly AevrixDataPaths _paths;
    private readonly ProjectRepository _projectRepository;
    private BrowserProjectChoice? _selectedProject;
    private WebView2? _webView;
    private int _loadGeneration;

    public ResearchBrowserView()
    {
        InitializeComponent();
        _paths = AevrixDataPaths.ForCurrentUser().EnsureCreated();
        _projectRepository = new ProjectRepository(_paths);
        Loaded += ResearchBrowserView_Loaded;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var selectedId = _selectedProject?.ProjectId;
            var projects = await _projectRepository.ListAsync(cancellationToken);
            var choices = projects
                .Where(item => item.Project.Domain == ProjectDomain.Web
                    && item.Project.EntryPoint is not null
                    && item.BrowserPolicy is not null)
                .Select(item => new BrowserProjectChoice(item))
                .ToArray();

            ProjectSelector.ItemsSource = choices;
            ProjectSelector.IsEnabled = choices.Length > 0;

            if (choices.Length == 0)
            {
                _selectedProject = null;
                ProjectSelector.SelectedIndex = -1;
                await TearDownBrowserAsync();
                SetStatus("Nenhum projeto Web com política de navegador foi encontrado.");
                SetNotice(
                    InfoBarSeverity.Warning,
                    "Research Browser bloqueado",
                    "Crie um projeto Web com política válida antes de abrir uma sessão interativa.");
                return;
            }

            var index = selectedId is Guid id
                ? Array.FindIndex(choices, item => item.ProjectId == id)
                : -1;
            ProjectSelector.SelectedIndex = index >= 0 ? index : 0;
        }
        catch (Exception ex)
        {
            _selectedProject = null;
            ProjectSelector.ItemsSource = Array.Empty<BrowserProjectChoice>();
            ProjectSelector.IsEnabled = false;
            await TearDownBrowserAsync();
            SetStatus("Falha ao carregar projetos.");
            SetNotice(
                InfoBarSeverity.Error,
                "Projetos indisponíveis",
                $"A leitura local falhou de forma fechada ({ex.GetType().Name}).");
        }
    }

    private async void ResearchBrowserView_Loaded(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProject = ProjectSelector.SelectedItem as BrowserProjectChoice;
        if (_selectedProject is null)
        {
            await TearDownBrowserAsync();
            SetStatus("Nenhum projeto carregado.");
            return;
        }

        await StartSelectedProjectAsync();
    }

    private async Task StartSelectedProjectAsync()
    {
        if (_selectedProject is null)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        var envelope = _selectedProject.Envelope;
        var project = envelope.Project;
        var policy = envelope.BrowserPolicy;
        var entryPoint = project.EntryPoint;

        if (policy is null || entryPoint is null)
        {
            await TearDownBrowserAsync();
            SetNotice(InfoBarSeverity.Error, "Projeto inválido", "O projeto não possui política e entrada Web completas.");
            return;
        }

        var initialDecision = ResearchBrowserNavigationGate.Evaluate(policy, entryPoint);
        if (!initialDecision.Allowed)
        {
            await TearDownBrowserAsync();
            SetNotice(InfoBarSeverity.Error, "Entrada bloqueada", initialDecision.Detail);
            return;
        }

        try
        {
            SetStatus("Inicializando ambiente WebView2 isolado...");
            HomeButton.IsEnabled = false;
            ReloadButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            ForwardButton.IsEnabled = false;

            await TearDownBrowserAsync(incrementGeneration: false);
            if (generation != _loadGeneration)
            {
                return;
            }

            var profilePath = _paths.ProjectBrowserProfile(project.Id, project.TargetId);
            Directory.CreateDirectory(profilePath);
            var environment = await CoreWebView2Environment.CreateAsync(null, profilePath);
            if (generation != _loadGeneration)
            {
                return;
            }

            var webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            BrowserHost.Children.Add(webView);
            BrowserEmptyState.Visibility = Visibility.Collapsed;
            _webView = webView;

            await webView.EnsureCoreWebView2Async(environment);
            if (generation != _loadGeneration || _webView != webView)
            {
                return;
            }

            ConfigureCore(webView.CoreWebView2);
            AttachCoreHandlers(webView.CoreWebView2);
            AddressInput.Text = SanitizeForDisplay(entryPoint);
            HomeButton.IsEnabled = true;
            ReloadButton.IsEnabled = true;
            SetNotice(
                InfoBarSeverity.Informational,
                "Sessão isolada pronta",
                $"Perfil local separado para “{project.Name}”. Navegação permanece limitada aos hosts explicitamente autorizados.");
            NavigateIfAllowed(entryPoint);
        }
        catch (Exception ex)
        {
            await TearDownBrowserAsync(incrementGeneration: false);
            SetStatus("WebView2 indisponível.");
            SetNotice(
                InfoBarSeverity.Error,
                "Research Browser não inicializado",
                $"O host WebView2 falhou de forma fechada ({ex.GetType().Name}). Nenhuma navegação foi tratada como autorizada.");
        }
    }

    private void ConfigureCore(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
    }

    private void AttachCoreHandlers(CoreWebView2 core)
    {
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.DownloadStarting += Core_DownloadStarting;
        core.ProcessFailed += Core_ProcessFailed;
    }

    private void DetachCoreHandlers(CoreWebView2 core)
    {
        core.NavigationStarting -= Core_NavigationStarting;
        core.NavigationCompleted -= Core_NavigationCompleted;
        core.NewWindowRequested -= Core_NewWindowRequested;
        core.DownloadStarting -= Core_DownloadStarting;
        core.ProcessFailed -= Core_ProcessFailed;
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_selectedProject?.Envelope.BrowserPolicy is not ResearchBrowserPolicy policy
            || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var candidate))
        {
            e.Cancel = true;
            SetNotice(InfoBarSeverity.Error, "Navegação bloqueada", "A navegação não pôde ser vinculada a uma política e URI válidas.");
            return;
        }

        var decision = ResearchBrowserNavigationGate.Evaluate(policy, candidate);
        if (!decision.Allowed)
        {
            e.Cancel = true;
            SetStatus("Navegação bloqueada pela política.");
            SetNotice(InfoBarSeverity.Warning, "Limite do projeto preservado", decision.Detail);
            return;
        }

        AddressInput.Text = SanitizeForDisplay(candidate);
        SetStatus($"Abrindo {candidate.Host}...");
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_webView?.CoreWebView2 is not CoreWebView2 core)
        {
            return;
        }

        BackButton.IsEnabled = core.CanGoBack;
        ForwardButton.IsEnabled = core.CanGoForward;
        ReloadButton.IsEnabled = true;

        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var current))
        {
            AddressInput.Text = SanitizeForDisplay(current);
            SetStatus(e.IsSuccess
                ? $"Carregado: {current.Host}"
                : $"Falha de navegação: {current.Host}");
        }
        else
        {
            SetStatus(e.IsSuccess ? "Página carregada." : "Falha de navegação.");
        }
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_selectedProject?.Envelope.BrowserPolicy is not ResearchBrowserPolicy policy
            || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var candidate))
        {
            SetNotice(InfoBarSeverity.Warning, "Nova janela bloqueada", "O destino solicitado não é uma URI governável.");
            return;
        }

        var decision = ResearchBrowserNavigationGate.Evaluate(policy, candidate);
        if (!decision.Allowed)
        {
            SetNotice(InfoBarSeverity.Warning, "Popup bloqueado", decision.Detail);
            return;
        }

        NavigateIfAllowed(candidate);
    }

    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        SetNotice(
            InfoBarSeverity.Warning,
            "Download bloqueado",
            "Downloads ainda não possuem pipeline de evidência/quarentena homologado no Research Browser e são cancelados por padrão.");
    }

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        SetStatus("Processo WebView2 interrompido.");
        SetNotice(
            InfoBarSeverity.Error,
            "Sessão do navegador interrompida",
            $"O processo WebView2 falhou ({e.ProcessFailedKind}). A sessão não é considerada saudável até nova inicialização.");
    }

    private void OpenAddressButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(AddressInput.Text.Trim(), UriKind.Absolute, out var candidate))
        {
            SetNotice(InfoBarSeverity.Warning, "Endereço inválido", "Informe um endereço HTTPS absoluto.");
            return;
        }

        NavigateIfAllowed(candidate);
    }

    private void AddressInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OpenAddressButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webView?.CoreWebView2?.CanGoBack == true)
        {
            _webView.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_webView?.CoreWebView2?.CanGoForward == true)
        {
            _webView.CoreWebView2.GoForward();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
        => _webView?.CoreWebView2?.Reload();

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject?.Envelope.Project.EntryPoint is Uri entryPoint)
        {
            NavigateIfAllowed(entryPoint);
        }
    }

    private void NavigateIfAllowed(Uri candidate)
    {
        if (_selectedProject?.Envelope.BrowserPolicy is not ResearchBrowserPolicy policy
            || _webView?.CoreWebView2 is not CoreWebView2 core)
        {
            SetNotice(InfoBarSeverity.Warning, "Navegação indisponível", "Nenhuma sessão governada está pronta.");
            return;
        }

        var decision = ResearchBrowserNavigationGate.Evaluate(policy, candidate);
        if (!decision.Allowed)
        {
            SetNotice(InfoBarSeverity.Warning, "Navegação bloqueada", decision.Detail);
            return;
        }

        core.Navigate(candidate.AbsoluteUri);
    }

    private Task TearDownBrowserAsync(bool incrementGeneration = true)
    {
        if (incrementGeneration)
        {
            Interlocked.Increment(ref _loadGeneration);
        }

        if (_webView is not null)
        {
            if (_webView.CoreWebView2 is CoreWebView2 core)
            {
                DetachCoreHandlers(core);
            }
            BrowserHost.Children.Remove(_webView);
            _webView = null;
        }

        BrowserEmptyState.Visibility = Visibility.Visible;
        HomeButton.IsEnabled = false;
        ReloadButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        return Task.CompletedTask;
    }

    private void SetStatus(string text) => BrowserStatusText.Text = text;

    private void SetNotice(InfoBarSeverity severity, string title, string message)
    {
        BrowserNotice.Severity = severity;
        BrowserNotice.Title = title;
        BrowserNotice.Message = message;
        BrowserNotice.IsOpen = true;
    }

    private static string SanitizeForDisplay(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    private sealed class BrowserProjectChoice
    {
        public BrowserProjectChoice(ProjectEnvelope envelope) => Envelope = envelope;
        public ProjectEnvelope Envelope { get; }
        public Guid ProjectId => Envelope.Project.Id;
        public override string ToString() => Envelope.Project.Name;
    }
}