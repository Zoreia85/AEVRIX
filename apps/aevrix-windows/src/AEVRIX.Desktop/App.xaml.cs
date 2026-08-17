using System;
using System.IO;
using Aevrix.Core;
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
        var stateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AEVRIX",
            "UserData");
        var firstRunStore = new FirstRunAcceptanceStore(stateRoot);

        if (firstRunStore.IsAccepted())
        {
            OpenMainWindow();
            return;
        }

        _window = new FirstRunWindow(firstRunStore, OpenMainWindow);
        _window.Activate();
    }

    private void OpenMainWindow()
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
