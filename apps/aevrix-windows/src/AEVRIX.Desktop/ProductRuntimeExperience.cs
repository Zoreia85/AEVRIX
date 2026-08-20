using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AEVRIX.Desktop;

internal sealed class ProductRuntimeExperience
{
    private readonly MainWindow _window;
    private readonly NavigationView _navigation;
    private readonly Grid _contentHost;
    private readonly InvestigationRegistryStore _localRegistry = InvestigationRegistryStore.ForCurrentUser();
    private readonly GitHubDesktopConnectionService _gitHub = new();
    private readonly ScrollViewer _runtimeDashboard;
    private readonly StackPanel _runtimeList = new() { Spacing = 12 };
    private readonly TextBlock _runtimeSummary = new();
    private readonly TextBlock _githubStatus = new();
    private readonly TextBlock _githubActions = new();
    private readonly TextBlock _githubSha = new();
    private readonly TextBlock _githubSync = new();
    private readonly InfoBar _runtimeNotice = new();
    private readonly InfoBar _formRuntimeNotice = new();
    private bool _refreshInProgress;

    private ProductRuntimeExperience(MainWindow window)
    {
        _window = window;
        _navigation = FindDescendant<NavigationView>(window.Content)
            ?? throw new InvalidOperationException("AEVRIX NavigationView was not found for runtime binding.");
        _contentHost = _navigation.Content as Grid
            ?? throw new InvalidOperationException("AEVRIX content host is not a Grid.");

        _runtimeDashboard = BuildRuntimeDashboard();
        Panel.SetZIndex(_runtimeDashboard, 100);
        _contentHost.Children.Add(_runtimeDashboard);
        _navigation.SelectionChanged += Navigation_SelectionChanged;

        BindInvestigationForm();
        BindWhiteLabelForm();
    }

    public static void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _ = new ProductRuntimeExperience(window);
    }

    private void BindInvestigationForm()
    {
        var button = FindDescendants<Button>(_window.Content)
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), "Preparar investigação", StringComparison.Ordinal));
        if (button is null)
        {
            return;
        }

        if (button.Parent is Panel panel)
        {
            _formRuntimeNotice.IsOpen = false;
            _formRuntimeNotice.IsClosable = true;
            panel.Children.Add(_formRuntimeNotice);
        }
        button.Click += PrepareInvestigationRuntime_Click;
    }

    private void BindWhiteLabelForm()
    {
        var button = FindDescendants<Button>(_window.Content)
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), "Preparar reconstrução clean-room", StringComparison.Ordinal));
        if (button is not null)
        {
            button.Click += PrepareWhiteLabelRuntime_Click;
        }
    }

    private async void PrepareInvestigationRuntime_Click(object sender, RoutedEventArgs e)
    {
        await Task.Yield();
        try
        {
            var workspace = FindTextBox("Nome do projeto / workspace")?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return;
            }

            var entry = _localRegistry.Load()
                .FirstOrDefault(item => string.Equals(item.Workspace, workspace, StringComparison.Ordinal));
            if (entry is null)
            {
                // The existing governed form did not accept the draft, so runtime binding must not
                // create an alternate path around that validation.
                return;
            }

            var targetKind = ReadRequiredEnumTag<InvestigationTargetKind>("O que será investigado?");
            var strategy = ReadRequiredEnumTag<InvestigationStrategy>("Como o AEVRIX deve trabalhar?");
            var authorization = ReadRequiredStringTag("Qual é sua autorização sobre este alvo?");
            var sensitivity = ReadRequiredStringTag("Sensibilidade dos dados");
            var target = FindTextBox("Alvo principal")?.Text ?? string.Empty;
            var goals = FindTextBox("Objetivo")?.Text ?? string.Empty;
            var artifacts = ParseArtifacts(FindTextBoxStartingWith("Anexos / artefatos locais")?.Text);

            var draft = new InvestigationDraft(
                entry.Id,
                entry.Workspace,
                targetKind,
                strategy,
                authorization,
                target,
                goals,
                sensitivity,
                artifacts,
                entry.CreatedAtUtc);

            var runtime = await _window.RegisterInvestigationRuntimeAsync(draft);
            var reconciled = await _window.ReconcileInvestigationRuntimeAsync();
            var current = reconciled.FirstOrDefault(item => item.InvestigationId == runtime.InvestigationId) ?? runtime;

            _formRuntimeNotice.Severity = current.State == InvestigationRunState.Blocked
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            _formRuntimeNotice.Title = "Runtime local vinculado";
            _formRuntimeNotice.Message =
                $"{current.MissionId} • {Describe(current.State)} • {current.PercentComplete:F1}%. " +
                (current.Blocker ?? "EngineHost aceitou e persistiu a investigação.");
            _formRuntimeNotice.IsOpen = true;
        }
        catch (Exception ex)
        {
            _formRuntimeNotice.Severity = InfoBarSeverity.Error;
            _formRuntimeNotice.Title = "Runtime local não foi vinculado";
            _formRuntimeNotice.Message =
                $"A investigação local continua registrada, mas o EngineHost bloqueou o vínculo ({ex.GetType().Name}). Nenhum estado de execução foi inventado.";
            _formRuntimeNotice.IsOpen = true;
        }
    }

    private async void PrepareWhiteLabelRuntime_Click(object sender, RoutedEventArgs e)
    {
        await Task.Yield();
        try
        {
            var entry = _localRegistry.Load().FirstOrDefault(item => item.Strategy == InvestigationStrategy.ReconstructWhiteLabel);
            if (entry is null)
            {
                return;
            }

            var draft = new InvestigationDraft(
                entry.Id,
                entry.Workspace,
                InvestigationTargetKind.Other,
                InvestigationStrategy.ReconstructWhiteLabel,
                "clean-room",
                "blueprint-clean-room",
                "Reconstrução clean-room governada a partir de Blueprint aprovado.",
                "standard",
                [],
                entry.CreatedAtUtc);
            await _window.RegisterInvestigationRuntimeAsync(draft);
            await _window.ReconcileInvestigationRuntimeAsync();
        }
        catch
        {
            // The original whitelabel notice already reports the local draft. Runtime status is
            // surfaced in the operations dashboard; do not convert a runtime failure into success.
        }
    }

    private ScrollViewer BuildRuntimeDashboard()
    {
        var root = new StackPanel { Spacing = 18 };
        root.Children.Add(CreateEyebrow("PAINEL DE OPERAÇÕES • RUNTIME REAL"));
        root.Children.Add(CreateTitle("Investigações, fila, progresso e conexões"));
        root.Children.Add(CreateBody(
            "Este painel lê o EngineHost autenticado. Estados, porcentagens e blockers vêm do runtime persistido; nenhuma investigação é mostrada como ativa quando o motor não consegue prová-la."));

        var connectionGrid = new Grid { ColumnSpacing = 14 };
        connectionGrid.ColumnDefinitions.Add(new ColumnDefinition());
        connectionGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var enginePanel = new StackPanel { Spacing = 7 };
        enginePanel.Children.Add(CreateLabel("INVESTIGAÇÕES"));
        _runtimeSummary.FontSize = 22;
        _runtimeSummary.Text = "Carregando runtime...";
        enginePanel.Children.Add(_runtimeSummary);
        enginePanel.Children.Add(CreateMuted("Fila e budget são recalculados pelo EngineHost conforme a capacidade local, com teto de 10 e proteção contra starvation."));
        connectionGrid.Children.Add(CreateCard(enginePanel, 0));

        var githubPanel = new StackPanel { Spacing = 7 };
        githubPanel.Children.Add(CreateLabel("GITHUB / ACTIONS"));
        _githubStatus.FontSize = 20;
        _githubStatus.Text = "Verificando...";
        githubPanel.Children.Add(_githubStatus);
        _githubActions.Text = "Actions: aguardando prova";
        githubPanel.Children.Add(_githubActions);
        _githubSha.Text = "main: aguardando prova";
        githubPanel.Children.Add(CreateMutedContainer(_githubSha));
        _githubSync.Text = "Última sincronização: ainda não comprovada";
        githubPanel.Children.Add(CreateMutedContainer(_githubSync));
        connectionGrid.Children.Add(CreateCard(githubPanel, 1));
        root.Children.Add(connectionGrid);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var refresh = new Button { Content = "Atualizar painel" };
        refresh.Click += async (_, _) => await RefreshAsync(reconcile: false);
        var reconcile = new Button { Content = "Reconciliar fila" };
        reconcile.Click += async (_, _) => await RefreshAsync(reconcile: true);
        controls.Children.Add(refresh);
        controls.Children.Add(reconcile);
        root.Children.Add(controls);

        _runtimeNotice.IsOpen = false;
        _runtimeNotice.IsClosable = true;
        root.Children.Add(_runtimeNotice);
        root.Children.Add(_runtimeList);

        return new ScrollViewer
        {
            Visibility = Visibility.Collapsed,
            Background = ResourceBrush("AevrixSurfaceBrush"),
            Content = new Grid
            {
                MaxWidth = 1180,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(40, 32, 40, 48),
                Children = { root }
            }
        };
    }

    private async void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var route = args.SelectedItemContainer is NavigationViewItem item
            ? item.Tag?.ToString()
            : null;
        var show = string.Equals(route, "mission", StringComparison.Ordinal);
        _runtimeDashboard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            await RefreshAsync(reconcile: false);
        }
    }

    private async Task RefreshAsync(bool reconcile)
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            IReadOnlyList<InvestigationRuntimeRecord> records;
            try
            {
                records = reconcile
                    ? await _window.ReconcileInvestigationRuntimeAsync()
                    : await _window.ListInvestigationRuntimeAsync();
                RenderRuntime(records);
                _runtimeNotice.IsOpen = false;
            }
            catch (Exception ex)
            {
                records = Array.Empty<InvestigationRuntimeRecord>();
                _runtimeSummary.Text = "Runtime indisponível";
                _runtimeList.Children.Clear();
                _runtimeNotice.Severity = InfoBarSeverity.Error;
                _runtimeNotice.Title = "EngineHost não comprovado";
                _runtimeNotice.Message =
                    $"O painel não conseguiu ler o runtime autenticado ({ex.GetType().Name}). Estados de investigação permanecem indisponíveis.";
                _runtimeNotice.IsOpen = true;
            }

            var github = await _gitHub.ProbeAsync();
            RenderGitHub(github);
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void RenderRuntime(IReadOnlyList<InvestigationRuntimeRecord> records)
    {
        var running = records.Count(item => item.State == InvestigationRunState.Running);
        var queued = records.Count(item => item.State is InvestigationRunState.Queued or InvestigationRunState.Ready);
        var paused = records.Count(item => item.State == InvestigationRunState.Paused);
        var blocked = records.Count(item => item.State == InvestigationRunState.Blocked);
        var failed = records.Count(item => item.State == InvestigationRunState.Failed);
        var completed = records.Count(item => item.State == InvestigationRunState.Completed);
        var cancelled = records.Count(item => item.State == InvestigationRunState.Cancelled);

        _runtimeSummary.Text = records.Count == 0
            ? "Nenhuma investigação vinculada ao EngineHost"
            : $"{running} executando • {queued} em fila • {paused} pausadas • {blocked} bloqueadas • {failed} falhas • {completed} concluídas • {cancelled} canceladas";

        _runtimeList.Children.Clear();
        if (records.Count == 0)
        {
            _runtimeList.Children.Add(CreateCard(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    CreateSectionTitle("Nenhuma missão local ainda"),
                    CreateMuted("Use Nova investigação. Após a validação do formulário, o mesmo Investigation ID será ligado ao EngineHost autenticado.")
                }
            }));
            return;
        }

        foreach (var record in records.Take(50))
        {
            _runtimeList.Children.Add(BuildRuntimeCard(record));
        }
    }

    private Border BuildRuntimeCard(InvestigationRuntimeRecord record)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = record.Workspace,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{record.MissionId} • {Describe(record.TargetKind)} • {Describe(record.Strategy)}",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Describe(record.State)} • fase: {Describe(record.CurrentPhase)}" +
                   (record.QueuePosition > 0 ? $" • fila #{record.QueuePosition}" : string.Empty),
            FontSize = 18
        });
        panel.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = record.PercentComplete,
            Height = 8
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{record.PercentComplete:F1}% • ETA: {FormatEta(record.EstimatedRemaining)}",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(CreateMuted(
            $"Budget atual: CPU {record.Budget.CpuWeight}% relativo • memória até {FormatBytes(record.Budget.MemoryBytes)} • até {record.Budget.MaxParallelAgentPackages} pacotes de agentes em paralelo."));
        if (!string.IsNullOrWhiteSpace(record.Blocker))
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "Bloqueio atual",
                Message = record.Blocker
            });
        }
        panel.Children.Add(CreateMuted(
            $"Última atividade: {record.LastActivityAtUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} • evidências de progresso: {record.ProgressEvidence.Count}"));

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (record.State == InvestigationRunState.Paused)
        {
            var resume = new Button { Content = "Retomar" };
            resume.Click += async (_, _) => await ExecuteControlAsync(record.InvestigationId, "resume");
            controls.Children.Add(resume);
        }
        else if (record.State == InvestigationRunState.Blocked)
        {
            var retry = new Button { Content = "Tentar novamente" };
            retry.Click += async (_, _) => await ExecuteControlAsync(record.InvestigationId, "resume");
            controls.Children.Add(retry);
        }
        else if (record.State == InvestigationRunState.Running || record.State == InvestigationRunState.Queued)
        {
            var pause = new Button { Content = "Pausar" };
            pause.Click += async (_, _) => await ExecuteControlAsync(record.InvestigationId, "pause");
            controls.Children.Add(pause);
        }

        if (record.State is not (InvestigationRunState.Completed or InvestigationRunState.Cancelled))
        {
            var cancel = new Button { Content = "Cancelar" };
            cancel.Click += async (_, _) => await ExecuteControlAsync(record.InvestigationId, "cancel");
            controls.Children.Add(cancel);
        }
        if (controls.Children.Count > 0)
        {
            panel.Children.Add(controls);
        }

        return CreateCard(panel);
    }

    private async Task ExecuteControlAsync(Guid investigationId, string action)
    {
        try
        {
            switch (action)
            {
                case "pause":
                    await _window.PauseInvestigationRuntimeAsync(investigationId);
                    break;
                case "resume":
                    await _window.ResumeInvestigationRuntimeAsync(investigationId);
                    await _window.ReconcileInvestigationRuntimeAsync();
                    break;
                case "cancel":
                    await _window.CancelInvestigationRuntimeAsync(investigationId);
                    break;
                default:
                    throw new InvalidOperationException("Unknown runtime control action.");
            }
            await RefreshAsync(reconcile: false);
        }
        catch (Exception ex)
        {
            _runtimeNotice.Severity = InfoBarSeverity.Error;
            _runtimeNotice.Title = "Ação recusada pelo runtime";
            _runtimeNotice.Message =
                $"O EngineHost não confirmou a operação ({ex.GetType().Name}). O estado anterior foi preservado.";
            _runtimeNotice.IsOpen = true;
        }
    }

    private void RenderGitHub(GitHubConnectionSnapshot snapshot)
    {
        _githubStatus.Text = snapshot.ApiReachable
            ? snapshot.Authenticated
                ? $"GitHub conectado • {snapshot.Login}"
                : "GitHub público alcançável • autenticação pendente"
            : "GitHub indisponível";
        _githubActions.Text = snapshot.Actions.Readable
            ? $"Actions: {snapshot.Actions.WorkflowName ?? "sem execução"} • {snapshot.Actions.Status ?? "n/d"} • {snapshot.Actions.Conclusion ?? "sem conclusão"}"
            : $"Actions: não comprovado • {snapshot.Actions.Detail}";
        _githubSha.Text = snapshot.CanonicalSha is { Length: > 0 }
            ? $"main canônico observado: {snapshot.CanonicalSha}"
            : "main canônico: não comprovado";
        _githubSync.Text = snapshot.LastSuccessfulSyncAtUtc is { } sync
            ? $"Última sincronização comprovada: {sync.ToLocalTime():dd/MM/yyyy HH:mm:ss}"
            : "Última sincronização: não comprovada";
    }

    private TEnum ReadRequiredEnumTag<TEnum>(string header)
        where TEnum : struct, Enum
    {
        var combo = FindComboBox(header)
            ?? throw new InvalidOperationException($"Campo '{header}' não foi encontrado.");
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not TEnum value)
        {
            throw new InvalidOperationException($"Campo '{header}' precisa de uma seleção válida.");
        }
        return value;
    }

    private string ReadRequiredStringTag(string header)
    {
        var combo = FindComboBox(header)
            ?? throw new InvalidOperationException($"Campo '{header}' não foi encontrado.");
        if (combo.SelectedItem is not ComboBoxItem item || item.Tag is not string value || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Campo '{header}' precisa de uma seleção válida.");
        }
        return value;
    }

    private ComboBox? FindComboBox(string header)
        => FindDescendants<ComboBox>(_window.Content)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));

    private TextBox? FindTextBox(string header)
        => FindDescendants<TextBox>(_window.Content)
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));

    private TextBox? FindTextBoxStartingWith(string headerPrefix)
        => FindDescendants<TextBox>(_window.Content)
            .FirstOrDefault(item => item.Header?.ToString()?.StartsWith(headerPrefix, StringComparison.Ordinal) == true);

    private static IReadOnlyList<InvestigationInputArtifact> ParseArtifacts(string? text)
        => (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new InvestigationInputArtifact(Path.GetFileName(path), path))
            .ToArray();

    private static string Describe(InvestigationRunState state) => state switch
    {
        InvestigationRunState.Draft => "Rascunho",
        InvestigationRunState.Ready => "Pronta",
        InvestigationRunState.Queued => "Em fila",
        InvestigationRunState.Running => "Executando",
        InvestigationRunState.Paused => "Pausada",
        InvestigationRunState.Blocked => "Bloqueada",
        InvestigationRunState.Failed => "Falha",
        InvestigationRunState.Completed => "Concluída",
        InvestigationRunState.Cancelled => "Cancelada",
        _ => state.ToString()
    };

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

    private static string FormatEta(TimeSpan? eta)
    {
        if (eta is null)
        {
            return "indisponível — ainda não há evidência histórica suficiente";
        }
        if (eta.Value.TotalHours >= 1)
        {
            return $"aprox. {eta.Value.TotalHours:F1} h";
        }
        return $"aprox. {Math.Max(1, eta.Value.TotalMinutes):F0} min";
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:F1} GiB"
            : $"{bytes / 1024d / 1024d:F0} MiB";

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
            TextWrapping = TextWrapping.Wrap
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

    private static T? FindDescendant<T>(object? root)
        where T : DependencyObject
        => FindDescendants<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindDescendants<T>(object? root)
        where T : DependencyObject
    {
        if (root is T typed)
        {
            yield return typed;
        }
        if (root is not DependencyObject dependency)
        {
            yield break;
        }

        var count = VisualTreeHelper.GetChildrenCount(dependency);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(dependency, index);
            foreach (var found in FindDescendants<T>(child))
            {
                yield return found;
            }
        }
    }
}
