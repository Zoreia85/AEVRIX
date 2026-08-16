using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop.Views;

public sealed partial class BlueprintView : UserControl
{
    private readonly DesktopBlueprintExplorerService _explorer = new();
    private bool _loadingProjects;
    private bool _loadingCaptures;
    private bool _loadingBlueprint;

    public BlueprintView()
    {
        InitializeComponent();
        Loaded += BlueprintView_Loaded;
    }

    public async Task LoadProjectsAsync(CancellationToken cancellationToken = default)
    {
        if (_loadingProjects)
        {
            return;
        }

        _loadingProjects = true;
        SetBusy(true, "Carregando projetos…");
        try
        {
            var projects = await _explorer.ListProjectsAsync(cancellationToken);
            ProjectInput.Items.Clear();
            CaptureInput.Items.Clear();
            foreach (var project in projects)
            {
                ProjectInput.Items.Add(new ComboBoxItem
                {
                    Content = $"{project.Name} • {project.Status}",
                    Tag = project
                });
            }

            if (projects.Count == 0)
            {
                StatusText.Text = "Nenhum projeto local válido possui contexto para consulta de Blueprint.";
                ClearBlueprint();
                return;
            }

            ProjectInput.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Carregamento cancelado; nenhum blueprint foi inferido.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = $"O catálogo de Blueprint foi rejeitado ({ex.GetType().Name}).";
        }
        finally
        {
            _loadingProjects = false;
            SetBusy(false, null);
        }

        await LoadCapturesAsync(cancellationToken);
    }

    private async Task LoadCapturesAsync(CancellationToken cancellationToken = default)
    {
        if (_loadingProjects || _loadingCaptures)
        {
            return;
        }

        var project = GetSelectedProject();
        CaptureInput.Items.Clear();
        ClearBlueprint();
        if (project is null)
        {
            StatusText.Text = "Selecione um projeto antes de consultar exports de Blueprint.";
            return;
        }

        _loadingCaptures = true;
        SetBusy(true, "Procurando exports de Blueprint válidos…");
        try
        {
            var captures = await _explorer.ListCapturesAsync(project.Id, cancellationToken);
            foreach (var capture in captures)
            {
                CaptureInput.Items.Add(new ComboBoxItem
                {
                    Content = $"{capture.CaptureId} • {capture.LastModifiedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}",
                    Tag = capture
                });
            }

            if (captures.Count == 0)
            {
                StatusText.Text = "Nenhum export de Blueprint com manifesto e arquivo base foi encontrado para este projeto.";
                return;
            }

            CaptureInput.SelectedIndex = 0;
            StatusText.Text = $"{captures.Count} export(s) candidato(s) encontrado(s). Use 'Validar e carregar' para verificar identidade e SHA-256.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Busca cancelada; nenhum export foi inferido.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = $"Os exports foram rejeitados ({ex.GetType().Name}).";
        }
        finally
        {
            _loadingCaptures = false;
            SetBusy(false, null);
        }
    }

    private async Task LoadBlueprintAsync(CancellationToken cancellationToken = default)
    {
        if (_loadingBlueprint)
        {
            return;
        }

        var project = GetSelectedProject();
        var capture = GetSelectedCapture();
        if (project is null || capture is null)
        {
            StatusText.Text = "Selecione projeto e captura antes de validar o Blueprint.";
            return;
        }

        _loadingBlueprint = true;
        SetBusy(true, "Validando manifesto, identidade, SHA-256 e invariantes do Blueprint…");
        ClearBlueprint();
        try
        {
            var snapshot = await _explorer.LoadAsync(project.Id, capture.CaptureId, cancellationToken);
            ApplyBlueprint(snapshot);
            StatusText.Text = snapshot.Detail;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Validação cancelada; o Blueprint não foi exibido.";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or System.Text.Json.JsonException
            or ArgumentException)
        {
            StatusText.Text = $"Blueprint bloqueado: {ex.GetType().Name}. Nenhum modelo foi considerado válido.";
        }
        finally
        {
            _loadingBlueprint = false;
            SetBusy(false, null);
        }
    }

    private async void BlueprintView_Loaded(object sender, RoutedEventArgs e)
        => await LoadProjectsAsync();

    private async void ProjectInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProjects)
        {
            await LoadCapturesAsync();
        }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
        => await LoadBlueprintAsync();

    private DesktopBlueprintProject? GetSelectedProject()
        => (ProjectInput.SelectedItem as ComboBoxItem)?.Tag as DesktopBlueprintProject;

    private DesktopBlueprintCapture? GetSelectedCapture()
        => (CaptureInput.SelectedItem as ComboBoxItem)?.Tag as DesktopBlueprintCapture;

    private void SetBusy(bool busy, string? message)
    {
        ProjectInput.IsEnabled = !busy;
        CaptureInput.IsEnabled = !busy;
        LoadButton.IsEnabled = !busy;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private void ClearBlueprint()
    {
        ReadinessText.Text = "—";
        GradeText.Text = "—";
        RebuildText.Text = "Não verificado";
        EvidenceCountText.Text = "—";
        DimensionsList.Items.Clear();
        ConstraintsText.Text = "Nenhum blueprint validado foi carregado.";
        ModelCountsText.Text = "Arquitetura/API/UI/comportamento: —";
        HashText.Text = "SHA-256: —";
    }

    private void ApplyBlueprint(DesktopBlueprintSnapshot snapshot)
    {
        var blueprint = snapshot.Blueprint;
        var readiness = blueprint.Readiness;
        ReadinessText.Text = $"{readiness.OverallPercent:0.##}%";
        GradeText.Text = readiness.Grade;
        RebuildText.Text = readiness.ReadyForIndependentRebuild ? "Elegível" : "Bloqueado";
        EvidenceCountText.Text = blueprint.Evidence.Count.ToString();

        DimensionsList.Items.Clear();
        foreach (var dimension in readiness.Dimensions)
        {
            DimensionsList.Items.Add($"{dimension.Name}: {dimension.Percent:0.##}% • peso {dimension.Weight:P0}");
        }

        var constraints = new List<string>();
        constraints.AddRange(readiness.BlockingReasons.Select(item => $"Bloqueio: {item}"));
        constraints.AddRange(blueprint.Limitations.Select(item => $"Limitação: {item}"));
        constraints.AddRange(blueprint.OpenQuestions.Select(item => $"Pergunta aberta: {item}"));
        ConstraintsText.Text = constraints.Count == 0
            ? "Nenhum bloqueio, limitação ou pergunta aberta foi declarado no snapshot validado."
            : string.Join(Environment.NewLine, constraints);

        ModelCountsText.Text =
            $"Arquitetura: {blueprint.ArchitectureElements.Count} elementos / {blueprint.ArchitectureRelationships.Count} relações • workflows: {blueprint.Workflows.Count} • APIs: {blueprint.ApiEndpoints.Count} • UI: {blueprint.UiComponents.Count} • comportamentos: {blueprint.BehavioralModels.Count}";
        HashText.Text = $"SHA-256: {snapshot.BlueprintSha256} • captura: {snapshot.CaptureId}";
    }
}
