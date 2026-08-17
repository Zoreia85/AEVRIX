using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow
{
    private ResearchBrowserView? _researchBrowserView;
    private UIElement? _researchBrowserPlaceholder;

    internal void InitializeResearchBrowserSurface()
    {
        if (_researchBrowserView is not null)
        {
            return;
        }

        _researchBrowserPlaceholder = PlannedSectionView.Children.Count > 0
            ? PlannedSectionView.Children[0]
            : null;
        _researchBrowserView = new ResearchBrowserView
        {
            Visibility = Visibility.Collapsed
        };
        PlannedSectionView.Children.Add(_researchBrowserView);
        RootNavigation.SelectionChanged += ResearchBrowserNavigation_SelectionChanged;
    }

    private async void ResearchBrowserNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_researchBrowserView is null)
        {
            return;
        }

        var route = args.SelectedItemContainer is NavigationViewItem item
            ? item.Tag?.ToString()
            : null;
        var showBrowser = string.Equals(route, "browser", StringComparison.Ordinal);

        _researchBrowserView.Visibility = showBrowser
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (showBrowser)
        {
            if (_researchBrowserPlaceholder is not null)
            {
                _researchBrowserPlaceholder.Visibility = Visibility.Collapsed;
            }
            PlannedSectionView.Visibility = Visibility.Visible;
            await _researchBrowserView.RefreshAsync();
            return;
        }

        // ProjectCredentials owns the same planned host when route=projects. Do not override its placeholder state.
        if (!string.Equals(route, "projects", StringComparison.Ordinal)
            && _researchBrowserPlaceholder is not null)
        {
            _researchBrowserPlaceholder.Visibility = Visibility.Visible;
        }
    }
}