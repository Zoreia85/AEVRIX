using System;
using Aevrix.Core;
using Microsoft.UI.Xaml;

namespace AEVRIX.Desktop;

public sealed partial class FirstRunWindow : Window
{
    private readonly FirstRunAcceptanceStore _store;
    private readonly Action _continueToProduct;
    private bool _accepted;

    public FirstRunWindow(FirstRunAcceptanceStore store, Action continueToProduct)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _continueToProduct = continueToProduct ?? throw new ArgumentNullException(nameof(continueToProduct));

        InitializeComponent();
        Title = "AEVRIX — Primeira execução";
        Activated += FirstRunWindow_Activated;
    }

    private void FirstRunWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        Activated -= FirstRunWindow_Activated;

        try
        {
            _store.RecordPresentation();
        }
        catch (Exception ex)
        {
            AcceptFirstRunButton.IsEnabled = false;
            FirstRunConfirmCheckBox.IsEnabled = false;
            FirstRunErrorNotice.Message = $"Não foi possível registrar de forma auditável a primeira execução ({ex.GetType().Name}). O produto permanecerá bloqueado.";
            FirstRunErrorNotice.IsOpen = true;
        }
    }

    private void FirstRunConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        AcceptFirstRunButton.IsEnabled = FirstRunConfirmCheckBox.IsChecked == true && !FirstRunErrorNotice.IsOpen;
    }

    private void AcceptFirstRunButton_Click(object sender, RoutedEventArgs e)
    {
        AcceptFirstRunButton.IsEnabled = false;

        try
        {
            _store.Accept();
            if (!_store.IsAccepted())
            {
                throw new InvalidOperationException("Persisted first-run acceptance could not be revalidated.");
            }

            _accepted = true;
            _continueToProduct();
            Close();
        }
        catch (Exception ex)
        {
            FirstRunErrorNotice.Message = $"O aceite não pôde ser persistido e revalidado ({ex.GetType().Name}). O Command Center não será liberado.";
            FirstRunErrorNotice.IsOpen = true;
            AcceptFirstRunButton.IsEnabled = FirstRunConfirmCheckBox.IsChecked == true;
        }
    }

    private void DeclineFirstRunButton_Click(object sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }
}
