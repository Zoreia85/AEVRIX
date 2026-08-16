using System;
using System.IO;
using Aevrix.Core;
using Aevrix.EngineHost;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class MainWindow : Window
{
    private EngineHostSupervisor? _engineSupervisor;

    public MainWindow()
    {
        InitializeComponent();
        Title = "AEVRIX Desktop";
        Closed += MainWindow_Closed;
        ShowSection("home", "Command Center");
    }

    private void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            ShowSection("home", "Command Center");
            return;
        }

        var route = item.Tag?.ToString() ?? "home";
        var title = item.Content?.ToString() ?? "AEVRIX";
        ShowSection(route, title);
    }

    private void StartAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = NewInvestigationNavItem;
        ShowSection("new", "Nova investigação");
    }

    private async void VerifyEngineHostButton_Click(object sender, RoutedEventArgs e)
    {
        VerifyEngineHostButton.IsEnabled = false;
        EngineHostStatusText.Text = "Verificando";
        EngineHostDetailText.Text = "Iniciando sessão local autenticada e executando Ping real.";

        try
        {
            _engineSupervisor ??= CreateEngineSupervisor();
            await _engineSupervisor.StartAsync();

            var requestId = Guid.NewGuid().ToString("N");
            var response = await _engineSupervisor.SendAsync(new EnginePingCommand(requestId));

            if (!response.Success ||
                !string.Equals(response.Code, "pong", StringComparison.Ordinal) ||
                !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("EngineHost returned an invalid authenticated Ping response.");
            }

            EngineHostStatusText.Text = "Autenticado";
            EngineHostDetailText.Text = _engineSupervisor.ProcessId is int processId
                ? $"Ping real confirmado. Processo local supervisionado: PID {processId}."
                : "Ping real confirmado em sessão local supervisionada.";
        }
        catch (Exception ex)
        {
            EngineHostStatusText.Text = "Bloqueado";
            EngineHostDetailText.Text = $"A verificação falhou de forma fechada ({ex.GetType().Name}). Nenhum estado saudável foi inferido.";

            if (_engineSupervisor is not null)
            {
                await _engineSupervisor.DisposeAsync();
                _engineSupervisor = null;
            }
        }
        finally
        {
            VerifyEngineHostButton.IsEnabled = true;
        }
    }

    private void ValidateScopeButton_Click(object sender, RoutedEventArgs e)
    {
        PolicyUnavailableNotice.IsOpen = true;
    }

    private void BackToHomeButton_Click(object sender, RoutedEventArgs e)
    {
        RootNavigation.SelectedItem = HomeNavItem;
        ShowSection("home", "Command Center");
    }

    private static EngineHostSupervisor CreateEngineSupervisor()
    {
        var engineAssembly = typeof(EngineHostRuntime).Assembly.Location;
        return new EngineHostSupervisor(
            "dotnet",
            new[] { engineAssembly },
            startupTimeout: TimeSpan.FromSeconds(20),
            requestTimeout: TimeSpan.FromSeconds(5));
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_engineSupervisor is null)
        {
            return;
        }

        await _engineSupervisor.DisposeAsync();
        _engineSupervisor = null;
    }

    private void ShowSection(string route, string title)
    {
        var showHome = string.Equals(route, "home", StringComparison.Ordinal);
        var showNew = string.Equals(route, "new", StringComparison.Ordinal);

        CommandCenterView.Visibility = showHome ? Visibility.Visible : Visibility.Collapsed;
        NewInvestigationView.Visibility = showNew ? Visibility.Visible : Visibility.Collapsed;
        PlannedSectionView.Visibility = !showHome && !showNew
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!showHome && !showNew)
        {
            PlannedSectionTitle.Text = title;
        }
    }
}
