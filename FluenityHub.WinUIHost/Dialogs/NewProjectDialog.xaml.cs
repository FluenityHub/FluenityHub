using System.Diagnostics;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class NewProjectDialog : ContentDialog
{
    public string CreatedProjectPath { get; private set; } = string.Empty;
    public string CreatedProjectTitle { get; private set; } = string.Empty;
    public string SelectedVersion { get; private set; } = string.Empty;

    private readonly SourceControlService _sourceControlService = new();
    private readonly GitService _gitService = new();
    private readonly TemplateService _templateService = new();
    private readonly CustomTemplateInfo? _selectedTemplate;
    private readonly bool _isSourceControlEnabled;
    private bool _isAuthorized;
    private bool _isBusy;
    private bool _isInitialized;
    private int _authorizationRequestId;
    private bool _isApplyingSavedToken;
    private string? _appliedSavedToken;
    private string? _appliedSavedTokenProvider;

    public NewProjectDialog(
        IEnumerable<string> installedVersions,
        CustomTemplateInfo? selectedTemplate = null,
        string? initialVersion = null)
    {
        InitializeComponent();
        _selectedTemplate = selectedTemplate;
        _isSourceControlEnabled = new AppSettingsStore().Load().EnableSourceControl;

        SourceControlExpander.Visibility = _isSourceControlEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_isSourceControlEnabled)
        {
            SourceControlProviderComboBox.SelectedIndex = 0;
        }

        if (MainWindow.Instance is not null)
        {
            RequestedTheme = MainWindow.Instance.CurrentTheme;
        }

        LocationTextBox.Text =
            new UnityHubProjectSettingsService().GetProjectLocation();

        var versionsList = installedVersions.ToList();
        EditorVersionComboBox.ItemsSource = versionsList;

        if (_selectedTemplate is not null)
        {
            ProjectNameTextBox.Text = $"{_selectedTemplate.Name} Project";
            var matchingVersionIdx = versionsList.FindIndex(v => string.Equals(v, _selectedTemplate.EditorVersion, StringComparison.OrdinalIgnoreCase));
            if (matchingVersionIdx >= 0)
            {
                EditorVersionComboBox.SelectedIndex = matchingVersionIdx;
            }
            else if (versionsList.Count > 0)
            {
                EditorVersionComboBox.SelectedIndex = 0;
            }
        }
        else if (!string.IsNullOrWhiteSpace(initialVersion))
        {
            var matchingVersionIdx = versionsList.FindIndex(v => string.Equals(v, initialVersion, StringComparison.OrdinalIgnoreCase));
            if (matchingVersionIdx >= 0)
            {
                EditorVersionComboBox.SelectedIndex = matchingVersionIdx;
            }
            else if (versionsList.Count > 0)
            {
                EditorVersionComboBox.SelectedIndex = 0;
            }
        }
        else if (versionsList.Count > 0)
        {
            EditorVersionComboBox.SelectedIndex = 0;
        }

        UpdateSlugPreview();
        _isInitialized = true;
        ValidateInputs(showMessage: false);
    }

    private void OnProjectNameTextChanged(object sender, TextChangedEventArgs e)
    {
        var projectName = ProjectNameTextBox.Text?.Trim() ?? string.Empty;
        if (RepositoryNameTextBox is not null && (string.IsNullOrWhiteSpace(RepositoryNameTextBox.Text) || RepositoryNameTextBox.Tag as string == "auto"))
        {
            RepositoryNameTextBox.Text = projectName;
            RepositoryNameTextBox.Tag = "auto";
        }
        UpdateSlugPreview();
        ValidateInputs(showMessage: true);
    }

    private void OnRepositoryNameChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.FocusState != FocusState.Unfocused)
        {
            tb.Tag = "custom";
        }
        UpdateSlugPreview();
        ValidateInputs(showMessage: true);
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        ValidateInputs(showMessage: true);
    }

    private void OnInputSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ValidateInputs(showMessage: true);
    }

    private void UpdateSlugPreview()
    {
        if (SlugPreviewTextBlock is null) return;
        var name = RepositoryNameTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ProjectNameTextBox?.Text?.Trim() ?? "my-project";
        }

        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9\-]", "-").Trim('-');
        SlugPreviewTextBlock.Text = $"Slug preview: {slug}";
    }

    private void OnSourceControlProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceControlProviderComboBox is null || TokenSectionStackPanel is null || RepositoryDetailsStackPanel is null) return;
        _authorizationRequestId++;
        SetAuthorizationVisualState(false);
        var tag = (SourceControlProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        _isAuthorized = false;

        if (TokenInfoButton is not null)
        {
            ToolTipService.SetToolTip(TokenInfoButton, tag == "gitlab"
                ? "Your token is stored securely in your OS keychain.\n\nRequired Scopes:\n. api\n. read_user\n\nThis allows FluenityHub to create the repository and sync your initial commit via HTTPS."
                : "Your token is stored securely in your OS keychain.\n\nFor Classic Tokens:\n. Select repo scope\n\nFor Fine-grained Tokens:\n. Add the permissions Administration and Contents and set access to Read and Write.\n\nThese allow FluenityHub to create the repository and sync your initial commit via HTTPS.");
        }

        if (tag == "github" || tag == "gitlab")
        {
            TokenSectionStackPanel.Visibility = Visibility.Visible;
            CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            RepositoryDetailsStackPanel.Visibility = Visibility.Visible;
            HostedRepositoryNameStackPanel.Visibility = Visibility.Visible;
            HostedVisibilityStackPanel.Visibility = Visibility.Visible;
            HostedDescriptionStackPanel.Visibility = Visibility.Visible;
            SourceControlConfigurationExpander.Header = "Additional configuration";
            CreateInitialCommitCheckBox.Content = "Create and push initial commit";
            AutoFillSavedToken(tag);
        }
        else if (tag == "git")
        {
            TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            CustomGitRemoteStackPanel.Visibility = Visibility.Visible;
            RepositoryDetailsStackPanel.Visibility = Visibility.Visible;
            HostedRepositoryNameStackPanel.Visibility = Visibility.Collapsed;
            HostedVisibilityStackPanel.Visibility = Visibility.Collapsed;
            HostedDescriptionStackPanel.Visibility = Visibility.Collapsed;
            SourceControlConfigurationExpander.Header = "Git configuration";
            CreateInitialCommitCheckBox.Content = "Create initial commit";
            _isAuthorized = true;
        }
        else
        {
            TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            RepositoryDetailsStackPanel.Visibility = Visibility.Collapsed;
        }

        ValidateInputs(showMessage: false);
    }

    private void AutoFillSavedToken(string provider)
    {
        _isAuthorized = false;
        ShowUnlinkedState();

        var token = provider == "gitlab"
            ? CredentialService.GetGitLabToken()
            : CredentialService.GetGitHubToken();

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
        AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(token);
        if (!string.IsNullOrWhiteSpace(token))
        {
            OnAuthorizeClick(this, new RoutedEventArgs());
        }
    }

    private void OnTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        var selectedProvider =
            (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        var currentToken = TokenPasswordBox?.Password ?? string.Empty;
        var isSavedTokenEvent =
            string.Equals(selectedProvider, _appliedSavedTokenProvider, StringComparison.Ordinal) &&
            string.Equals(currentToken, _appliedSavedToken, StringComparison.Ordinal);
        if (!_isApplyingSavedToken && !isSavedTokenEvent)
        {
            _appliedSavedToken = null;
            _appliedSavedTokenProvider = null;
            _authorizationRequestId++;
            SetAuthorizationVisualState(false);
        }
        var token = currentToken.Trim();
        _isAuthorized = false;
        if (AuthorizeButton is not null)
        {
            AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(token);
        }

        // Token changes also occur when a saved Credential Locker token is applied while
        // switching providers. Recompute validity without surfacing unrelated
        // form errors such as a missing repository name.
        ValidateInputs(showMessage: false);
    }

    private void OnDisconnectTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        if (tag == "gitlab") CredentialService.RemoveGitLabToken();
        else CredentialService.RemoveGitHubToken();

        if (TokenPasswordBox is not null) TokenPasswordBox.Password = string.Empty;
        ShowUnlinkedState();
    }

    private void OnGetTokenClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var url = tag switch
        {
            "gitlab" => "https://gitlab.com/-/user_settings/personal_access_tokens?name=FluenityHub&scopes=api,read_user",
            _ => "https://github.com/settings/tokens/new?description=FluenityHub&scopes=repo"
        };

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch { }
    }

    private async void OnAuthorizeClick(object sender, RoutedEventArgs e)
    {
        var tag = (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        var token = TokenPasswordBox?.Password?.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            ShowAuthStatus("Please enter a personal access token.", isError: true);
            return;
        }

        await ValidateAndApplyTokenAsync(tag, token);
    }

    private async Task ValidateAndApplyTokenAsync(string tag, string token)
    {
        var requestId = ++_authorizationRequestId;
        _isAuthorized = false;
        ShowUnlinkedState();
        if (AuthorizeButton is not null) AuthorizeButton.IsEnabled = false;
        SetAuthorizationVisualState(true);
        ValidateInputs(showMessage: false);

        try
        {
            var (success, primaryOwner, _, error) =
                await _sourceControlService.AuthorizeTokenAsync(tag, token);
            if (requestId != _authorizationRequestId) return;

            if (success)
            {
                if (tag == "gitlab") CredentialService.SaveGitLabToken(token);
                else CredentialService.SaveGitHubToken(token);

                ShowLinkedState(primaryOwner);
            }
            else
            {
                ShowUnlinkedState();
                ShowAuthStatus(error, isError: true);
            }
        }
        catch (Exception ex)
        {
            if (requestId != _authorizationRequestId) return;
            ShowUnlinkedState();
            ShowAuthStatus($"Error: {ex.Message}", isError: true);
        }
        finally
        {
            if (requestId == _authorizationRequestId)
            {
                if (AuthorizeButton is not null)
                    AuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(TokenPasswordBox?.Password);
                SetAuthorizationVisualState(false);
                ValidateInputs(showMessage: false);
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

    private void ShowLinkedState(string username)
    {
        _isAuthorized = true;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Collapsed;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Visible;
        if (TokenUserTextBlock is not null) TokenUserTextBlock.Text = $"Connected as @{username}";
        if (AuthStatusTextBlock is not null) AuthStatusTextBlock.Visibility = Visibility.Collapsed;
        ValidateInputs(showMessage: false);
    }

    private void ShowUnlinkedState()
    {
        _isAuthorized = false;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;
        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
        ValidateInputs(showMessage: false);
    }

    private void ShowAuthStatus(string message, bool isError)
    {
        if (AuthStatusTextBlock is null) return;
        AuthStatusTextBlock.Text = message;
        AuthStatusTextBlock.Foreground = isError
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        AuthStatusTextBlock.Visibility = Visibility.Visible;
    }

    private async void OnBrowseLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MainWindow.Instance is null) return;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(MainWindow.Instance.AppWindow.Id)
            {
                Title = "Choose a project location",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                LocationTextBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnBrowseLocationClick failed: {ex}");
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var validationError = GetValidationError();
        if (validationError is not null)
        {
            args.Cancel = true;
            ShowError(validationError);
            ValidateInputs(showMessage: true);
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            var projectName = ProjectNameTextBox.Text?.Trim() ?? string.Empty;
            var parentFolder = LocationTextBox.Text?.Trim() ?? string.Empty;
            var selectedVersion = (EditorVersionComboBox.SelectedItem as string)!;

            var providerTag = GetSelectedSourceControlProvider();
            bool isPrivate = PrivateVisibilityRadioButton?.IsChecked == true;
            string repoName = RepositoryNameTextBox?.Text?.Trim() ?? projectName;
            string defaultBranch = DefaultBranchTextBox?.Text?.Trim() ?? "main";
            if (string.IsNullOrWhiteSpace(defaultBranch)) defaultBranch = "main";
            string description = RepositoryDescriptionTextBox?.Text?.Trim() ?? string.Empty;
            bool enableLfs = EnableGitLfsCheckBox?.IsChecked == true;
            bool doInitialCommit = CreateInitialCommitCheckBox?.IsChecked == true;
            string token = TokenPasswordBox?.Password?.Trim() ?? string.Empty;
            string customGitRemoteUrl = CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty;

            var projectPath = Path.Combine(parentFolder, projectName);

            SetCreationState(true);
            await Task.Yield();
            try
            {
                await Task.Run(async () =>
                {
                    if (_selectedTemplate is not null)
                    {
                        var created = await _templateService.CreateProjectFromTemplateAsync(_selectedTemplate, projectPath, selectedVersion);
                        if (!created)
                        {
                            throw new InvalidOperationException("The selected template could not be extracted.");
                        }
                    }
                    else
                    {
                        if (!Directory.Exists(parentFolder))
                        {
                            Directory.CreateDirectory(parentFolder);
                        }

                        Directory.CreateDirectory(projectPath);
                        Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
                        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));

                        var versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
                        File.WriteAllText(versionFile, $"m_EditorVersion: {selectedVersion}\r\nm_EditorVersionWithRevision: {selectedVersion}\r\n");
                    }

                    // Configure the selected source-control provider.
                    if (providerTag != "none")
                    {
                        var remoteUrl = providerTag == "git"
                            ? customGitRemoteUrl
                            : string.Empty;
                        string? credentialUser = null;
                        string? credentialPassword = null;

                        if (providerTag == "github" || providerTag == "gitlab")
                        {
                            var (authOk, primaryUser, _, authError) =
                                await _sourceControlService.AuthorizeTokenAsync(providerTag, token);
                            if (!authOk)
                            {
                                throw new InvalidOperationException(authError);
                            }

                            var (createOk, createdRemoteUrl, createError) =
                                await _sourceControlService.CreateRemoteRepositoryAsync(
                                    providerTag,
                                    token,
                                    primaryUser,
                                    primaryUser,
                                    repoName,
                                    isPrivate,
                                    description);
                            if (!createOk || string.IsNullOrWhiteSpace(createdRemoteUrl))
                            {
                                throw new InvalidOperationException(createError);
                            }

                            remoteUrl = createdRemoteUrl;
                            credentialUser = providerTag == "gitlab" ? "oauth2" : "x-access-token";
                            credentialPassword = token;
                            if (providerTag == "gitlab") CredentialService.SaveGitLabToken(token);
                            else CredentialService.SaveGitHubToken(token);
                        }

                        var (gitOk, gitMessage) = await _gitService.InitAndSetupUnityGitAsync(
                            projectPath,
                            remoteUrl,
                            defaultBranch,
                            enableLfs,
                            doInitialCommit,
                            credentialUser,
                            credentialPassword);
                        if (!gitOk)
                        {
                            throw new InvalidOperationException(gitMessage);
                        }
                    }
                });

                CreatedProjectPath = projectPath;
                CreatedProjectTitle = projectName;
                SelectedVersion = selectedVersion;
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                ShowError($"Failed to create project files: {ex.Message}");
            }
            finally
            {
                SetCreationState(false);
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            Debug.WriteLine($"OnPrimaryButtonClick failed: {ex}");
            ShowError($"Failed to create project: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ValidateInputs(bool showMessage)
    {
        if (!_isInitialized)
        {
            return;
        }

        var error = GetValidationError();
        IsPrimaryButtonEnabled = !_isBusy && error is null;

        if (ValidationInfoBar is null)
        {
            return;
        }

        ValidationInfoBar.Message = error ?? string.Empty;
        ValidationInfoBar.IsOpen = showMessage && error is not null;
    }

    private string? GetValidationError()
    {
        var projectName = ProjectNameTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return "Enter a project name.";
        }

        if (projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            projectName.EndsWith(' ') ||
            projectName.EndsWith('.'))
        {
            return "The project name contains characters that Windows cannot use in a folder name.";
        }

        var parentFolder = LocationTextBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parentFolder))
        {
            return "Choose a project location.";
        }

        string projectPath;
        try
        {
            projectPath = Path.GetFullPath(Path.Combine(parentFolder, projectName));
        }
        catch
        {
            return "Enter a valid project location.";
        }

        if (Directory.Exists(projectPath) || File.Exists(projectPath))
        {
            return "A project with this name already exists in the selected location.";
        }

        if (EditorVersionComboBox?.SelectedItem is not string)
        {
            return "Select an installed Unity Editor version.";
        }

        var provider = GetSelectedSourceControlProvider();
        if (provider == "none")
        {
            return null;
        }

        if ((provider == "github" || provider == "gitlab")
            && string.IsNullOrWhiteSpace(RepositoryNameTextBox?.Text))
        {
            return "Enter a repository name.";
        }

        if (string.IsNullOrWhiteSpace(DefaultBranchTextBox?.Text))
        {
            return "Enter a default branch name.";
        }

        var customRemoteUrl = CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty;
        if (provider == "git"
            && !string.IsNullOrWhiteSpace(customRemoteUrl)
            && !GitService.IsValidRemoteUrl(customRemoteUrl))
        {
            return "Enter a valid HTTPS or SSH Git remote URL, or leave it blank for local Git.";
        }

        if ((provider == "github" || provider == "gitlab") &&
            (!_isAuthorized || string.IsNullOrWhiteSpace(TokenPasswordBox?.Password)))
        {
            return "Authorize a personal access token before creating the project.";
        }

        return null;
    }

    private string GetSelectedSourceControlProvider()
        => _isSourceControlEnabled
            ? (SourceControlProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "none"
            : "none";

    private void SetCreationState(bool isBusy)
    {
        _isBusy = isBusy;
        CreationProgressInfoBar.IsOpen = isBusy;
        IsPrimaryButtonEnabled = false;
        if (!isBusy)
        {
            ValidateInputs(showMessage: false);
        }
    }

    private void ShowError(string message)
    {
        ValidationInfoBar.Message = message;
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.IsOpen = true;
    }
}
