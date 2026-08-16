using Microsoft.UI.Xaml;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "AEVRIX Desktop";
    }

    private void StartAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        IncrementNotice.IsOpen = true;
    }
}
