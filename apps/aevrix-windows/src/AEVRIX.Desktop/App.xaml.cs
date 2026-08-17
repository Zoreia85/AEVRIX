using Microsoft.UI.Xaml;

namespace AEVRIX.Desktop;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var mainWindow = new MainWindow();
        mainWindow.InitializeProjectCredentialsSurface();
        _window = mainWindow;
        _window.Activate();
    }
}
