using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop.Views;

public sealed partial class FirstRunView : UserControl
{
    private readonly DesktopFirstRunService _firstRun = new();
    private bool _identityBusy;

    public FirstRunView()
    {
        InitializeComponent();
        Loaded += FirstRunView_Loaded;
    }

    public void SetEngineStatus(string state, string detail)
    {
        EngineStateText.Text = string.IsNullOrWhiteSpace(state) ? "Não verificado" : state;
        EngineDetailText.Text = string.IsNullOrWhiteSpace(detail)
            ? "Nenhuma prova autenticada foi fornecida pelo Command Center."
            : detail;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var state = await _firstRun.ReadLocalStateAsync(cancellationToken);
        ApplyIdentityState(state);
    }

    private async void FirstRunView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            IdentityStateText.Text = "Cancelada";
            IdentityDetailText.Text = "Leitura do estado local cancelada; nenhum estado foi inferido.";
        }
    }

    private async void PrepareIdentityButton_Click(object sender, RoutedEventArgs e)
    {
        if (_identityBusy)
        {
            return;
        }

        _identityBusy = true;
        PrepareIdentityButton.IsEnabled = false;
        IdentityProgress.IsActive = true;
        IdentityProgress.Visibility = Visibility.Visible;
        IdentityStateText.Text = "Verificando…";
        IdentityDetailText.Text = "Validando Windows CNG/TPM e a chave ECDSA P-256 não exportável.";

        try
        {
            var state = await _firstRun.PrepareOrVerifyTpmIdentityAsync();
            ApplyIdentityState(state);
        }
        catch (OperationCanceledException)
        {
            IdentityStateText.Text = "Cancelada";
            IdentityDetailText.Text = "Operação cancelada; enrollment remoto não foi tentado.";
        }
        finally
        {
            PrepareIdentityButton.IsEnabled = true;
            IdentityProgress.IsActive = false;
            IdentityProgress.Visibility = Visibility.Collapsed;
            _identityBusy = false;
        }
    }

    private void ApplyIdentityState(DesktopFirstRunIdentityState state)
    {
        IdentityStateText.Text = state.State;
        IdentityDetailText.Text = state.Detail;

        if (string.IsNullOrWhiteSpace(state.KeyId))
        {
            IdentityMetadataText.Text = "Nenhum metadado criptográfico local verificado.";
            return;
        }

        var suffix = state.KeyId.Length > 12 ? state.KeyId[^12..] : state.KeyId;
        var prepared = state.PreparedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "desconhecido";
        IdentityMetadataText.Text = $"Tier: {state.SecurityTier ?? "desconhecido"} • Key ID …{suffix} • preparado em {prepared}.";
    }
}
