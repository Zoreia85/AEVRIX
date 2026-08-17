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
        try
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
        }
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("app-initialize", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
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
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("app-launch", ex);
            throw;
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        StartupFailureReporter.TryWrite("unhandled-ui", args.Exception);
        // Deliberately do not set Handled=true: startup/runtime faults remain fail-closed.
    }

    private void OpenMainWindow()
    {
        var mainWindow = new MainWindow();
        mainWindow.InitializeProjectCredentialsSurface();
        mainWindow.InitializeResearchBrowserSurface();
        _window = mainWindow;
        _window.Activate();
    }
}
