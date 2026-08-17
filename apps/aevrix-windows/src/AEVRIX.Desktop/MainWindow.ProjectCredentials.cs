using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow
{
    private ProjectCredentialsView? _projectCredentialsView;
    private UIElement? _plannedSectionPlaceholder;

    internal void InitializeProjectCredentialsSurface()
    {
        if (_projectCredentialsView is not null)
        {
            return;
        }

        _plannedSectionPlaceholder = PlannedSectionView.Children.Count > 0
            ? PlannedSectionView.Children[0]
            : null;
        _projectCredentialsView = new ProjectCredentialsView
        {
            Visibility = Visibility.Collapsed
        };
        PlannedSectionView.Children.Add(_projectCredentialsView);
        RootNavigation.SelectionChanged += ProjectCredentialsNavigation_SelectionChanged;
    }

    private async void ProjectCredentialsNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_projectCredentialsView is null)
        {
            return;
        }

        var route = args.SelectedItemContainer is NavigationViewItem item
            ? item.Tag?.ToString()
            : null;
        var showProjects = string.Equals(route, "projects", StringComparison.Ordinal);

        _projectCredentialsView.Visibility = showProjects
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_plannedSectionPlaceholder is not null)
        {
            _plannedSectionPlaceholder.Visibility = showProjects
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (!showProjects)
        {
            return;
        }

        PlannedSectionView.Visibility = Visibility.Visible;
        await _projectCredentialsView.RefreshAsync();
    }
}
