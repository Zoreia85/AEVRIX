using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop.Views;

public sealed partial class EvidenceView : UserControl
{
    private readonly DesktopEvidenceExplorerService _explorer = new();
    private bool _loadingProjects;
    private bool _loadingEvidence;
    private bool _verifying;
    private DesktopEvidenceArtifact? _selected;

    public EvidenceView()
    {
        InitializeComponent();
        Loaded += EvidenceView_Loaded;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loadingProjects)
        {
            return;
        }

        _loadingProjects = true;
        SetBusy(true, "Carregando projetos elegíveis…");
        try
        {
            var previousId = GetSelectedProject()?.Id;
            var projects = await _explorer.ListProjectsAsync(cancellationToken);
            ProjectInput.Items.Clear();
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
                StatusText.Text = "Nenhum projeto local válido está disponível para o Evidence Explorer.";
                EvidenceList.Items.Clear();
                ClearSelection();
                return;
            }

            var selectedIndex = 0;
            if (previousId is Guid expected)
            {
                for (var index = 0; index < ProjectInput.Items.Count; index++)
                {
                    if ((ProjectInput.Items[index] as ComboBoxItem)?.Tag is DesktopEvidenceProject candidate
                        && candidate.Id == expected)
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }
            ProjectInput.SelectedIndex = selectedIndex;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Carregamento cancelado; nenhum estado foi inferido.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = $"A lista de projetos foi rejeitada ({ex.GetType().Name}).";
        }
        finally
        {
            _loadingProjects = false;
            SetBusy(false, null);
        }

        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_loadingProjects || _loadingEvidence)
        {
            return;
        }

        var project = GetSelectedProject();
        if (project is null)
        {
            StatusText.Text = "Selecione um projeto antes de consultar o índice.";
            EvidenceList.Items.Clear();
            ClearSelection();
            return;
        }

        _loadingEvidence = true;
        SetBusy(true, "Lendo índice de evidências…");
        EvidenceList.Items.Clear();
        ClearSelection();
        try
        {
            var state = await _explorer.LoadProjectAsync(project.Id, GetSelectedClassification(), cancellationToken);
            StatusText.Text = state.Detail;
            foreach (var artifact in state.Artifacts)
            {
                EvidenceList.Items.Add(BuildArtifactItem(artifact));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Leitura cancelada; nenhum artefato foi inferido.";
        }
        finally
        {
            _loadingEvidence = false;
            SetBusy(false, null);
        }
    }

    private async void EvidenceView_Loaded(object sender, RoutedEventArgs e)
        => await LoadAsync();

    private async void ProjectInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProjects)
        {
            await RefreshAsync();
        }
    }

    private async void ClassificationInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProjects)
        {
            await RefreshAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private void EvidenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = (EvidenceList.SelectedItem as ListViewItem)?.Tag as DesktopEvidenceArtifact;
        ApplySelection();
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_verifying || _selected is null)
        {
            return;
        }

        var project = GetSelectedProject();
        if (project is null)
        {
            SelectionDetailText.Text = "Verificação bloqueada: nenhum projeto está selecionado.";
            return;
        }

        _verifying = true;
        VerifyButton.IsEnabled = false;
        SelectionDetailText.Text = "Calculando SHA-256 do artefato no armazenamento local…";
        try
        {
            var state = await _explorer.VerifyAsync(project.Id, _selected);
            SelectionDetailText.Text = state.Detail;
        }
        catch (OperationCanceledException)
        {
            SelectionDetailText.Text = "Verificação cancelada; integridade não foi inferida.";
        }
        finally
        {
            _verifying = false;
            VerifyButton.IsEnabled = _selected is not null;
        }
    }

    private DesktopEvidenceProject? GetSelectedProject()
        => (ProjectInput.SelectedItem as ComboBoxItem)?.Tag as DesktopEvidenceProject;

    private EvidenceClassification? GetSelectedClassification()
    {
        var tag = (ClassificationInput.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "sanitized" => EvidenceClassification.Sanitized,
            "knowledge" => EvidenceClassification.NeutralKnowledge,
            "quarantine" => EvidenceClassification.Quarantine,
            _ => null
        };
    }

    private void SetBusy(bool busy, string? message)
    {
        RefreshButton.IsEnabled = !busy;
        ProjectInput.IsEnabled = !busy;
        ClassificationInput.IsEnabled = !busy;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
    }

    private void ClearSelection()
    {
        _selected = null;
        EvidenceList.SelectedItem = null;
        SelectionDetailText.Text = "Selecione uma evidência para inspecionar metadados e verificar integridade.";
        VerifyButton.IsEnabled = false;
    }

    private void ApplySelection()
    {
        if (_selected is null)
        {
            ClearSelection();
            return;
        }

        var quarantine = _selected.IsQuarantine
            ? " • QUARENTENA: conteúdo não será aberto nesta superfície"
            : string.Empty;
        SelectionDetailText.Text =
            $"{_selected.EvidenceId} • {_selected.Classification} • {_selected.Kind} • {_selected.MediaType} • {FormatBytes(_selected.SizeBytes)} • captura {_selected.CaptureId} • armazenado {_selected.StoredAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz} • SHA-256 {_selected.Sha256}{quarantine}";
        VerifyButton.IsEnabled = !_verifying;
    }

    private static ListViewItem BuildArtifactItem(DesktopEvidenceArtifact artifact)
    {
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(4, 10, 4, 10) };
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
        if (bytes < 1024) return $"{bytes} B";
        var value = bytes / 1024d;
        if (value < 1024) return $"{value:0.##} KiB";
        value /= 1024d;
        if (value < 1024) return $"{value:0.##} MiB";
        return $"{value / 1024d:0.##} GiB";
    }
}
