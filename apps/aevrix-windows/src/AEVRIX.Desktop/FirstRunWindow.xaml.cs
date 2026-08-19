using System;
using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class FirstRunWindow : Window
{
    private readonly FirstRunAcceptanceStore _store;
    private readonly Action _continueToProduct;
    private readonly CheckBox _confirmCheckBox;
    private readonly Button _acceptButton;
    private readonly TextBlock _errorNotice;

    public FirstRunWindow(FirstRunAcceptanceStore store, Action continueToProduct)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _continueToProduct = continueToProduct ?? throw new ArgumentNullException(nameof(continueToProduct));

        // Do not call InitializeComponent here. The installed self-contained WinUI path
        // has repeatedly failed while parsing FirstRunWindow.xaml. The mandatory first-run
        // gate is therefore materialized with WinUI controls directly so the security and
        // acceptance contract remains fail-closed without depending on runtime XAML parsing.
        _confirmCheckBox = CreateConfirmationCheckBox();
        _acceptButton = CreateAcceptButton();
        _errorNotice = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        Content = BuildFirstRunSurface();
        Title = "AEVRIX — Primeira execução";
        Activated += FirstRunWindow_Activated;
    }

    private UIElement BuildFirstRunSurface()
    {
        var root = new Grid
        {
            Padding = new Thickness(40)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = new StackPanel
        {
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 18
        };

        content.Children.Add(new TextBlock
        {
            Text = "PRIMEIRA EXECUÇÃO",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Autorização e termos operacionais",
            FontSize = 34,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "Antes de usar o AEVRIX, confirme as condições operacionais desta versão.",
            FontSize = 16,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(CreateNotice(
            "NOT_HOMOLOGATED — Esta versão permanece em validação. Estados sem prova continuam bloqueados e nenhum aceite nesta tela concede autorização de investigação, autenticação remota ou autoridade de execução."));
        content.Children.Add(CreateNotice(
            "Condições desta versão\n\n" +
            "• Use o AEVRIX somente em sistemas, dados e ambientes próprios ou explicitamente autorizados.\n" +
            "• O produto opera fail-closed: ausência de evidência, autenticação ou política válida não é convertida em estado saudável.\n" +
            "• O aceite abaixo confirma apenas ciência destas condições locais de uso. Políticas de missão, credenciais, escopo e permissões continuam independentes.\n" +
            "• O AEVRIX registra evidências operacionais necessárias à auditabilidade da execução."));
        content.Children.Add(_confirmCheckBox);
        content.Children.Add(_errorNotice);

        var scroll = new ScrollViewer
        {
            Content = content
        };
        Grid.SetRow(scroll, 0);
        root.Children.Add(scroll);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 12,
            Margin = new Thickness(0, 24, 0, 0)
        };

        var declineButton = new Button
        {
            Content = "Sair"
        };
        AutomationProperties.SetAutomationId(declineButton, "AevrixFirstRunDecline");
        AutomationProperties.SetName(declineButton, "Sair sem aceitar as condições");
        declineButton.Click += DeclineFirstRunButton_Click;

        actions.Children.Add(declineButton);
        actions.Children.Add(_acceptButton);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);

        return root;
    }

    private static Border CreateNotice(string text)
    {
        return new Border
        {
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private CheckBox CreateConfirmationCheckBox()
    {
        var checkBox = new CheckBox
        {
            Content = "Li e compreendi as condições operacionais desta versão e desejo continuar."
        };
        AutomationProperties.SetAutomationId(checkBox, "AevrixFirstRunConfirm");
        AutomationProperties.SetName(checkBox, "Confirmar condições da primeira execução");
        checkBox.Checked += FirstRunConfirmCheckBox_Changed;
        checkBox.Unchecked += FirstRunConfirmCheckBox_Changed;
        return checkBox;
    }

    private Button CreateAcceptButton()
    {
        var button = new Button
        {
            Content = "Aceitar e continuar",
            IsEnabled = false
        };
        AutomationProperties.SetAutomationId(button, "AevrixFirstRunAccept");
        AutomationProperties.SetName(button, "Aceitar condições e continuar");
        button.Click += AcceptFirstRunButton_Click;
        return button;
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
            _acceptButton.IsEnabled = false;
            _confirmCheckBox.IsEnabled = false;
            ShowError($"Não foi possível registrar de forma auditável a primeira execução ({ex.GetType().Name}). O produto permanecerá bloqueado.");
        }
    }

    private void FirstRunConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _acceptButton.IsEnabled = _confirmCheckBox.IsChecked == true && _errorNotice.Visibility != Visibility.Visible;
    }

    private void AcceptFirstRunButton_Click(object sender, RoutedEventArgs e)
    {
        _acceptButton.IsEnabled = false;

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
            ShowError($"O aceite não pôde ser persistido e revalidado ({ex.GetType().Name}). O Command Center não será liberado.");
            _acceptButton.IsEnabled = _confirmCheckBox.IsChecked == true;
        }
    }

    private void ShowError(string message)
    {
        _errorNotice.Text = message;
        _errorNotice.Visibility = Visibility.Visible;
    }

    private void DeclineFirstRunButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
