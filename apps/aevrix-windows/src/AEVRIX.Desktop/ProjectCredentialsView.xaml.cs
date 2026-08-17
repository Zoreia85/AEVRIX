using Aevrix.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEVRIX.Desktop;

public sealed partial class ProjectCredentialsView : UserControl
{
    private readonly ProjectRepository _projectRepository;
    private readonly ProjectCredentialVault _credentialVault;
    private ProjectChoice? _selectedProject;

    public ProjectCredentialsView()
    {
        InitializeComponent();
        var paths = AevrixDataPaths.ForCurrentUser().EnsureCreated();
        _projectRepository = new ProjectRepository(paths);
        _credentialVault = new ProjectCredentialVault(
            paths,
            new WindowsCredentialManagerProjectSecretStore());
        Loaded += ProjectCredentialsView_Loaded;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var currentProjectId = _selectedProject?.ProjectId;
            var projects = await _projectRepository.ListAsync(cancellationToken);
            var choices = projects.Select(project => new ProjectChoice(project)).ToArray();

            ProjectSelector.ItemsSource = choices;
            ProjectSelector.IsEnabled = choices.Length > 0;

            if (choices.Length == 0)
            {
                _selectedProject = null;
                ProjectSelector.SelectedIndex = -1;
                ProjectDetailText.Text = "Nenhum projeto local foi encontrado. Crie ou importe um projeto antes de cadastrar acessos.";
                CredentialListView.ItemsSource = Array.Empty<CredentialListItem>();
                CredentialEmptyText.Visibility = Visibility.Visible;
                SaveCredentialButton.IsEnabled = false;
                return;
            }

            var restoredIndex = currentProjectId is Guid id
                ? Array.FindIndex(choices, choice => choice.ProjectId == id)
                : -1;
            ProjectSelector.SelectedIndex = restoredIndex >= 0 ? restoredIndex : 0;
        }
        catch (Exception ex)
        {
            _selectedProject = null;
            ProjectSelector.ItemsSource = Array.Empty<ProjectChoice>();
            ProjectSelector.IsEnabled = false;
            SaveCredentialButton.IsEnabled = false;
            ShowResult(
                InfoBarSeverity.Error,
                "Projetos indisponíveis",
                $"A leitura dos projetos locais falhou de forma fechada ({ex.GetType().Name}).");
        }
    }

    private async void ProjectCredentialsView_Loaded(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProject = ProjectSelector.SelectedItem as ProjectChoice;
        if (_selectedProject is null)
        {
            ProjectDetailText.Text = "Nenhum projeto selecionado.";
            SaveCredentialButton.IsEnabled = false;
            CredentialListView.ItemsSource = Array.Empty<CredentialListItem>();
            CredentialEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var project = _selectedProject.Envelope.Project;
        ProjectDetailText.Text = project.Domain == ProjectDomain.Web && project.EntryPoint is not null
            ? $"{project.Domain} • {project.EntryPoint.Host} • Target: {project.TargetId}"
            : $"{project.Domain} • Target: {project.TargetId}";
        SaveCredentialButton.IsEnabled = true;
        await RefreshCredentialsAsync();
    }

    private async void RefreshCredentialsButton_Click(object sender, RoutedEventArgs e)
        => await RefreshCredentialsAsync();

    private async Task RefreshCredentialsAsync()
    {
        if (_selectedProject is null)
        {
            CredentialListView.ItemsSource = Array.Empty<CredentialListItem>();
            CredentialEmptyText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var descriptors = await _credentialVault.ListAsync(_selectedProject.ProjectId);
            var items = descriptors.Select(descriptor => new CredentialListItem(descriptor)).ToArray();
            CredentialListView.ItemsSource = items;
            CredentialEmptyText.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            CredentialListView.ItemsSource = Array.Empty<CredentialListItem>();
            CredentialEmptyText.Visibility = Visibility.Visible;
            ShowResult(
                InfoBarSeverity.Error,
                "Cofre local bloqueado",
                $"As credenciais deste projeto não puderam ser listadas ({ex.GetType().Name}). Nenhum segredo foi inferido.");
        }
    }

    private async void SaveCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null)
        {
            ShowResult(InfoBarSeverity.Warning, "Selecione um projeto", "O login precisa pertencer explicitamente a um projeto.");
            return;
        }

        if (!Uri.TryCreate(LoginUriInput.Text.Trim(), UriKind.Absolute, out var loginUri))
        {
            ShowResult(InfoBarSeverity.Warning, "URL inválida", "Informe a URL HTTPS completa da tela de login.");
            return;
        }

        SaveCredentialButton.IsEnabled = false;
        try
        {
            var descriptor = await _credentialVault.AddAsync(
                _selectedProject.ProjectId,
                CredentialLabelInput.Text,
                loginUri,
                UserNameInput.Text,
                PasswordInput.Password,
                makeDefaultForLoginUri: DefaultCredentialCheckBox.IsChecked == true);

            ClearSecretInputs();
            CredentialLabelInput.Text = string.Empty;
            LoginUriInput.Text = descriptor.CanonicalLoginUri;
            DefaultCredentialCheckBox.IsChecked = true;
            ShowResult(
                InfoBarSeverity.Success,
                "Login salvo neste PC",
                "A credencial foi vinculada ao projeto e à URL informada. Usuário e senha não foram gravados no manifesto do projeto.");
            await RefreshCredentialsAsync();
        }
        catch (Exception ex)
        {
            ClearSecretInputs();
            ShowResult(
                InfoBarSeverity.Error,
                "Credencial não salva",
                $"O Windows ou a política local rejeitou a operação ({ex.GetType().Name}). Nenhum estado de sucesso foi assumido.");
        }
        finally
        {
            SaveCredentialButton.IsEnabled = _selectedProject is not null;
        }
    }

    private async void SetDefaultCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || CredentialListView.SelectedItem is not CredentialListItem selected)
        {
            ShowResult(InfoBarSeverity.Warning, "Selecione um login", "Escolha a conta que será usada como padrão para a URL correspondente.");
            return;
        }

        try
        {
            await _credentialVault.SetDefaultAsync(_selectedProject.ProjectId, selected.Descriptor.CredentialId);
            ShowResult(
                InfoBarSeverity.Success,
                "Conta padrão atualizada",
                "O AEVRIX poderá selecionar esta conta quando houver uma execução autorizada e a navegação atingir a URL correspondente.");
            await RefreshCredentialsAsync();
        }
        catch (Exception ex)
        {
            ShowResult(
                InfoBarSeverity.Error,
                "Padrão não alterado",
                $"A alteração foi bloqueada ({ex.GetType().Name}).");
        }
    }

    private async void DeleteCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || CredentialListView.SelectedItem is not CredentialListItem selected)
        {
            ShowResult(InfoBarSeverity.Warning, "Selecione um login", "Escolha a credencial que será removida deste computador.");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Excluir credencial deste PC?",
            Content = $"A conta “{selected.Label}” será removida do cofre local deste projeto. O AEVRIX não poderá recuperá-la depois desta ação.",
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _credentialVault.DeleteAsync(_selectedProject.ProjectId, selected.Descriptor.CredentialId);
            ShowResult(
                InfoBarSeverity.Success,
                "Credencial excluída",
                "O registro foi removido do cofre local deste computador.");
            await RefreshCredentialsAsync();
        }
        catch (Exception ex)
        {
            ShowResult(
                InfoBarSeverity.Error,
                "Não foi possível excluir",
                $"A remoção falhou de forma fechada ({ex.GetType().Name}).");
        }
    }

    private void ClearSecretInputs()
    {
        UserNameInput.Text = string.Empty;
        PasswordInput.Password = string.Empty;
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        CredentialResultNotice.Severity = severity;
        CredentialResultNotice.Title = title;
        CredentialResultNotice.Message = message;
        CredentialResultNotice.IsOpen = true;
    }

    private sealed class ProjectChoice
    {
        public ProjectChoice(ProjectEnvelope envelope) => Envelope = envelope;
        public ProjectEnvelope Envelope { get; }
        public Guid ProjectId => Envelope.Project.Id;
        public override string ToString() => Envelope.Project.Name;
    }

    private sealed class CredentialListItem
    {
        public CredentialListItem(ProjectCredentialDescriptor descriptor) => Descriptor = descriptor;
        public ProjectCredentialDescriptor Descriptor { get; }
        public string Label => Descriptor.Label;
        public string CanonicalLoginUri => Descriptor.CanonicalLoginUri;
        public string DefaultDescription => Descriptor.IsDefaultForLoginUri
            ? "Conta padrão para esta URL"
            : "Conta alternativa";
    }
}
