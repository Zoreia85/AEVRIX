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
        MainWindow mainWindow;
        try
        {
            mainWindow = new MainWindow();
        }
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("main-window-initialize", ex);
            throw;
        }

        _window = mainWindow;
        try
        {
            _window.Activate();
        }
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("main-window-activate", ex);
            throw;
        }

        // These modules are not prerequisites for displaying the governed Command Center.
        // Initialize them only after the main shell is active so an optional surface cannot
        // prevent first-run acceptance from transitioning into the product shell.
        try
        {
            mainWindow.InitializeProjectCredentialsSurface();
        }
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("project-credentials-initialize", ex);
        }

        try
        {
            mainWindow.InitializeResearchBrowserSurface();
        }
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("research-browser-initialize", ex);
        }

        try
        {
            ProductOperationsExperience.Attach(mainWindow);
        }
        catch (Exception ex)
        {
            StartupFailureReporter.TryWrite("product-operations-experience-initialize", ex);
        }
    }
}
