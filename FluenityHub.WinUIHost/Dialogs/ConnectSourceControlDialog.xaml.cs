using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class ConnectSourceControlDialog : ContentDialog
{
    private readonly SourceControlService _sourceControlService = new();
    private readonly GitService _gitService = new();
    private readonly UnityProjectInfo _project;

    public string CreatedRemoteUrl { get; private set; } = string.Empty;
    public string SelectedProvider { get; private set; } = "github";
    public string RepositoryName { get; private set; } = string.Empty;
    public string OrganizationName { get; private set; } = string.Empty;

    private string _primaryUser = string.Empty;
    private bool _isAuthorized = false;
    private int _authorizationRequestId;
    private bool _isApplyingSavedToken;
    private string? _appliedSavedToken;
    private string? _appliedSavedTokenProvider;

    public ConnectSourceControlDialog(UnityProjectInfo project)
    {
        InitializeComponent();
        _project = project;

        RepoNameTextBox.Text = project.Title;
        UpdateSlugPreview();
        AutoFillSavedToken();
        UpdateInfoTooltips();
    }

    private void UpdateInfoTooltips()
    {
        var provider = SelectedProvider;
        if (TokenInfoButton is not null)
        {
            if (provider == "gitlab")
            {
                ToolTipService.SetToolTip(TokenInfoButton, "Your token is stored securely in your OS keychain.\n\nRequired Scopes:\n• api\n• read_user\n\nThis allows FluenityHub to create the repository and sync your initial commit via HTTPS.");
            }
            else
            {
                ToolTipService.SetToolTip(TokenInfoButton, "Your token is stored securely in your OS keychain.\n\nFor Classic Tokens:\n• Select repo scope\n\nFor Fine-grained Tokens:\n• Add the permissions Administration and Contents and set access to Read and Write.\n\nThese allow FluenityHub to create the repository and sync your initial commit via HTTPS.");
            }
        }

        if (RepoNameInfoButton is not null)
        {
            if (provider == "gitlab")
            {
                ToolTipService.SetToolTip(RepoNameInfoButton, "Name of the project to create and sync the workspace to.\n\nGitLab limits the full path (namespace/project-name) to 255 characters, so the available length for your project name depends on the namespace selected.");
            }
            else
            {
                ToolTipService.SetToolTip(RepoNameInfoButton, "Name of the repository to create and sync the workspace to. Maximum 100 characters.");
            }
        }
    }

    private void AutoFillSavedToken()
    {
        try
        {
            _isAuthorized = false;
            IsPrimaryButtonEnabled = false;

            if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
            if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;

            if (OwnerComboBox is not null) OwnerComboBox.Items.Clear();

            var provider = SelectedProvider;
            string token = string.Empty;
            if (provider == "github")
            {
                token = CredentialService.GetGitHubToken();
            }
            else if (provider == "gitlab")
            {
                token = CredentialService.GetGitLabToken();
            }

            if (TokenPasswordBox is not null)
            {
                _appliedSavedToken = token;
                _appliedSavedTokenProvider = provider;
                _isApplyingSavedToken = true;
                try
                {
                    TokenPasswordBox.Password = token;
                }
                finally
                {
                    _isApplyingSavedToken = false;
                }
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                if (AuthorizeButton is not null) AuthorizeButton.IsEnabled = true;
                OnAuthorizeClick(this, new RoutedEventArgs());
            }
            else if (AuthorizeButton is not null)
            {
                AuthorizeButton.IsEnabled = false;
            }

            UpdatePrimaryButtonState();
        }
        catch
        {
            // Ignore
        }
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _authorizationRequestId++;
        SetAuthorizationVisualState(false);
        var provider = (ProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        SelectedProvider = provider;

        if (provider == "git")
        {
            if (TokenSectionStackPanel is not null) TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            if (CustomRemoteUrlStackPanel is not null) CustomRemoteUrlStackPanel.Visibility = Visibility.Visible;
            if (HostedRepositoryStackPanel is not null) HostedRepositoryStackPanel.Visibility = Visibility.Collapsed;
            if (OwnerStackPanel is not null) OwnerStackPanel.Visibility = Visibility.Collapsed;
            if (VisibilityStackPanel is not null) VisibilityStackPanel.Visibility = Visibility.Collapsed;
            if (DescriptionStackPanel is not null) DescriptionStackPanel.Visibility = Visibility.Collapsed;
            if (AdditionalConfigurationExpander is not null) AdditionalConfigurationExpander.Header = "Git configuration";
            _isAuthorized = true;
            UpdateCustomGitPushState();
        }
        else
        {
            if (TokenSectionStackPanel is not null) TokenSectionStackPanel.Visibility = Visibility.Visible;
            if (CustomRemoteUrlStackPanel is not null) CustomRemoteUrlStackPanel.Visibility = Visibility.Collapsed;
            if (HostedRepositoryStackPanel is not null) HostedRepositoryStackPanel.Visibility = Visibility.Visible;
            if (OwnerStackPanel is not null) OwnerStackPanel.Visibility = Visibility.Visible;
            if (VisibilityStackPanel is not null) VisibilityStackPanel.Visibility = Visibility.Visible;
            if (DescriptionStackPanel is not null) DescriptionStackPanel.Visibility = Visibility.Visible;
            if (AdditionalConfigurationExpander is not null) AdditionalConfigurationExpander.Header = "Additional configuration";
            if (PushChangesStackPanel is not null) PushChangesStackPanel.Visibility = Visibility.Visible;
            _isAuthorized = false;
            AutoFillSavedToken();
        }

        UpdateInfoTooltips();
        if (OwnerComboBox is not null)
        {
            OwnerComboBox.Items.Clear();
        }
        UpdatePrimaryButtonState();
    }

    private void OnGetTokenClick(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        var url = provider == "gitlab"
            ? "https://gitlab.com/-/user_settings/personal_access_tokens?name=FluenityHub&scopes=api,read_user"
            : "https://github.com/settings/tokens/new?description=FluenityHub&scopes=repo";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore
        }
    }

    private void OnTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        var token = TokenPasswordBox?.Password ?? string.Empty;
        var isSavedTokenEvent =
            string.Equals(SelectedProvider, _appliedSavedTokenProvider, StringComparison.Ordinal) &&
            string.Equals(token, _appliedSavedToken, StringComparison.Ordinal);
        if (!_isApplyingSavedToken && !isSavedTokenEvent)
        {
            _appliedSavedToken = null;
            _appliedSavedTokenProvider = null;
            _authorizationRequestId++;
            SetAuthorizationVisualState(false);
        }
        _isAuthorized = false;
        if (AuthorizeButton is not null && TokenPasswordBox is not null)
        {
            AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenPasswordBox.Password);
        }

        UpdatePrimaryButtonState();
    }

    private async void OnAuthorizeClick(object sender, RoutedEventArgs e)
    {
        var requestId = 0;
        try
        {
            var token = TokenPasswordBox.Password;
            var provider = (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
            SelectedProvider = provider;

            if (string.IsNullOrWhiteSpace(token))
            {
                ShowStatus("Please enter your Personal Access Token.", InfoBarSeverity.Warning);
                return;
            }

            requestId = ++_authorizationRequestId;
            if (AuthorizeButton is not null) AuthorizeButton.IsEnabled = false;
            SetAuthorizationVisualState(true);
            var (success, primaryUser, owners, error) =
                await _sourceControlService.AuthorizeTokenAsync(provider, token);
            if (requestId != _authorizationRequestId) return;

            if (success)
            {
                _primaryUser = primaryUser;
                _isAuthorized = true;

                OwnerComboBox.Items.Clear();
                foreach (var owner in owners)
                {
                    OwnerComboBox.Items.Add(owner);
                }

                if (OwnerComboBox.Items.Count > 0)
                {
                    OwnerComboBox.SelectedIndex = 0;
                }

                if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Collapsed;
                if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Visible;
                if (TokenUserTextBlock is not null) TokenUserTextBlock.Text = $"Connected as @{primaryUser}";
                if (provider == "github")
                {
                    CredentialService.SaveGitHubToken(token);
                }
                else
                {
                    CredentialService.SaveGitLabToken(token);
                }
                UpdatePrimaryButtonState();
            }
            else
            {
                if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
                if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;
                ShowStatus(error, InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            if (requestId != 0 && requestId != _authorizationRequestId) return;
            if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
            if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (requestId != 0 && requestId == _authorizationRequestId)
            {
                if (AuthorizeButton is not null) AuthorizeButton.IsEnabled = true;
                SetAuthorizationVisualState(false);
            }
        }
    }

    private void SetAuthorizationVisualState(bool isAuthorizing)
    {
        if (AuthorizeButton is not null)
            AuthorizeButton.Visibility = isAuthorizing ? Visibility.Collapsed : Visibility.Visible;
        if (AuthorizeLoadingPanel is not null)
            AuthorizeLoadingPanel.Visibility = isAuthorizing ? Visibility.Visible : Visibility.Collapsed;
        if (AuthorizeProgressRing is not null)
            AuthorizeProgressRing.IsActive = isAuthorizing;
    }

    private void OnDisconnectTokenClick(object sender, RoutedEventArgs e)
    {
        var provider = SelectedProvider;
        if (provider == "github")
        {
            CredentialService.RemoveGitHubToken();
        }
        else if (provider == "gitlab")
        {
            CredentialService.RemoveGitLabToken();
        }

        _isAuthorized = false;
        IsPrimaryButtonEnabled = false;
        TokenPasswordBox.Password = string.Empty;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;

        UpdatePrimaryButtonState();
    }

    private void OnRepoNameTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSlugPreview();
        UpdatePrimaryButtonState();
    }

    private void UpdatePrimaryButtonState()
    {
        if (string.Equals(SelectedProvider, "git", StringComparison.Ordinal))
        {
            IsPrimaryButtonEnabled = true;
            return;
        }

        IsPrimaryButtonEnabled = _isAuthorized
            && !string.IsNullOrWhiteSpace(RepoNameTextBox?.Text);
    }

    private void OnCustomRemoteUrlTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCustomGitPushState();
    }

    private void UpdateCustomGitPushState()
    {
        if (PushChangesStackPanel is null || PushChangesCheckBox is null || CustomRemoteUrlTextBox is null)
        {
            return;
        }

        var hasRemote = !string.IsNullOrWhiteSpace(CustomRemoteUrlTextBox.Text);
        PushChangesStackPanel.Visibility = hasRemote ? Visibility.Visible : Visibility.Collapsed;
        if (!hasRemote)
        {
            PushChangesCheckBox.IsChecked = false;
        }
    }

    private void UpdateSlugPreview()
    {
        var slug = SourceControlService.Slugify(RepoNameTextBox.Text);
        if (SlugPreviewTextBlock is not null)
        {
            SlugPreviewTextBlock.Text = $"Slug preview: {slug}";
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();

        try
        {
            if (!GitService.IsGitInstalled())
            {
                args.Cancel = true;
                ShowStatus("Git executable was not found on your system. Please install Git for Windows.", InfoBarSeverity.Error);
                return;
            }

            var repoName = RepoNameTextBox.Text;
            var provider = (ProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
            var token = TokenPasswordBox.Password;

            if (provider != "git" && (!_isAuthorized || string.IsNullOrWhiteSpace(token)))
            {
                args.Cancel = true;
                ShowStatus("Please click 'Authorize' to authenticate your Personal Access Token first.", InfoBarSeverity.Warning);
                return;
            }

            if (provider != "git" && string.IsNullOrWhiteSpace(repoName))
            {
                args.Cancel = true;
                ShowStatus("Repository name is required.", InfoBarSeverity.Warning);
                return;
            }

            var owner = OwnerComboBox.SelectedItem as string ?? _primaryUser;
            var isPrivate = PrivateVisibilityRadioButton.IsChecked == true;
            var description = RepoDescriptionTextBox.Text ?? string.Empty;
            var branchName = string.IsNullOrWhiteSpace(BranchNameTextBox.Text) ? "main" : BranchNameTextBox.Text.Trim();
            var enableLfs = EnableGitLfsCheckBox.IsChecked == true;
            var pushChanges = PushChangesCheckBox.IsChecked == true;

            string remoteUrl;
            string? credentialUser = null;
            string? credentialPassword = null;
            if (provider == "git")
            {
                remoteUrl = CustomRemoteUrlTextBox.Text?.Trim() ?? string.Empty;
                repoName = TryGetRepositoryName(remoteUrl) ?? _project.Title;
            }
            else
            {
                var (createSuccess, createdUrl, createError) = await _sourceControlService.CreateRemoteRepositoryAsync(
                    provider,
                    token,
                    _primaryUser,
                    owner,
                    repoName,
                    isPrivate,
                    description);

                if (!createSuccess)
                {
                    args.Cancel = true;
                    ShowStatus(createError, InfoBarSeverity.Error);
                    return;
                }

                remoteUrl = createdUrl;
                credentialUser = provider == "gitlab" ? "oauth2" : "x-access-token";
                credentialPassword = token;
            }

            var (gitSuccess, gitMessage) = await _gitService.InitAndSetupUnityGitAsync(
                _project.Path,
                remoteUrl,
                branchName,
                enableLfs,
                pushChanges && !string.IsNullOrWhiteSpace(remoteUrl),
                credentialUser,
                credentialPassword);

            if (!gitSuccess)
            {
                args.Cancel = true;
                ShowStatus(gitMessage, InfoBarSeverity.Error);
                return;
            }

            CreatedRemoteUrl = remoteUrl;
            SelectedProvider = provider;
            RepositoryName = repoName;
            OrganizationName = owner;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowStatus($"Error connecting to source control: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static string? TryGetRepositoryName(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var normalized = remoteUrl.TrimEnd('/');
        var separator = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf(':'));
        var name = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (DialogInfoBar is not null)
        {
            DialogInfoBar.Message = message;
            DialogInfoBar.Severity = severity;
            DialogInfoBar.IsOpen = true;
        }
    }

    private void HideStatus()
    {
        if (DialogInfoBar is not null)
        {
            DialogInfoBar.IsOpen = false;
        }
    }
}
