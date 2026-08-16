using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop.Views;

public sealed partial class ProjectsView : UserControl
{
    private readonly DesktopProjectCatalogService _catalog = new();
    private bool _loading;

    public ProjectsView()
    {
        InitializeComponent();
        Loaded += ProjectsView_Loaded;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Text = "Lendo o repositório local canônico…";
        ProjectsList.Items.Clear();

        try
        {
            var state = await _catalog.ListAsync(cancellationToken);
            StatusText.Text = state.Detail;
            foreach (var project in state.Projects)
            {
                ProjectsList.Items.Add(BuildProjectItem(project));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Atualização cancelada; nenhum estado foi inferido.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            _loading = false;
        }
    }

    private async void ProjectsView_Loaded(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private static ListViewItem BuildProjectItem(DesktopProjectSummary project)
    {
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(4, 10, 4, 10) };
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
                Text = "Atenção local: há quarentena, bloqueio ou falha registrada. Nenhum estado saudável é inferido.",
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
        if (bytes < 1024) return $"{bytes} B";
        var value = bytes / 1024d;
        if (value < 1024) return $"{value:0.##} KiB";
        value /= 1024d;
        if (value < 1024) return $"{value:0.##} MiB";
        return $"{value / 1024d:0.##} GiB";
    }
}
