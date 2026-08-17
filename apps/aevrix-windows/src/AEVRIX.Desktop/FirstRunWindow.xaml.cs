using System;
using Aevrix.Core;
using Microsoft.UI.Xaml;

namespace AEVRIX.Desktop;

public sealed partial class FirstRunWindow : Window
{
    private readonly FirstRunAcceptanceStore _store;
    private readonly Action _continueToProduct;

    public FirstRunWindow(FirstRunAcceptanceStore store, Action continueToProduct)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _continueToProduct = continueToProduct ?? throw new ArgumentNullException(nameof(continueToProduct));

        InitializeComponent();
        Title = "AEVRIX — Primeira execução";

        // Record the auditable first-run surface deterministically once the window has been
        // constructed successfully. Relying on Window.Activated made the audit marker dependent
        // on desktop-session focus semantics, which are not guaranteed on hosted Windows runners.
        // Failure remains fail-closed: operational navigation is still never created and all
        // acceptance controls are disabled if the presentation cannot be persisted.
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
        Close();
    }
}
