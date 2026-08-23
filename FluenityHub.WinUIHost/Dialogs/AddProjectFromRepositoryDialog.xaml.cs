using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class AddProjectFromRepositoryDialog : ContentDialog
{
    private readonly SourceControlService _sourceControlService = new();
    private readonly List<RepositoryItem> _fetchedRepositories = new();
    private readonly List<BranchItem> _fetchedBranches = new();

    public string SelectedProvider { get; private set; } = "github";
    public string DownloadedProjectPath { get; private set; } = string.Empty;

    private string _currentToken = string.Empty;
    private bool _isAuthorized = false;
    private int _authorizationRequestId;
    private bool _isApplyingSavedToken;
    private string? _appliedSavedToken;
    private string? _appliedSavedTokenProvider;
    private string _genericGitBaseLocation = string.Empty;
    private string _lastSuggestedGitLocation = string.Empty;

    public AddProjectFromRepositoryDialog()
    {
        InitializeComponent();
        SetDefaultLocation();
        UpdateInfoTooltips();
        AutoFillSavedToken();
    }

    private void UpdateInfoTooltips()
    {
        var provider = SelectedProvider;
        if (TokenInfoButton is not null)
        {
            if (provider == "gitlab")
            {
                ToolTipService.SetToolTip(TokenInfoButton, "Your token is stored securely in your OS keychain.\n\nRequired Scopes:\n• api\n• read_user\n\nThis allows FluenityHub to list and clone your repositories via HTTPS.");
            }
            else
            {
                ToolTipService.SetToolTip(TokenInfoButton, "Your token is stored securely in your OS keychain.\n\nFor Classic Tokens:\n• Select repo scope\n\nFor Fine-grained Tokens:\n• Grant repository Contents read access.\n\nThese allow FluenityHub to list and clone your repositories via HTTPS.");
            }
        }
    }

    private void SetDefaultLocation()
    {
        try
        {
            LocationTextBox.Text =
                new UnityHubProjectSettingsService().GetProjectLocation();
        }
        catch
        {
            // Ignore default path errors
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
            if (DialogInfoBar is not null) DialogInfoBar.IsOpen = false;

            RepositoryComboBox.Items.Clear();
            BranchComboBox.Items.Clear();
            _fetchedRepositories.Clear();
            _fetchedBranches.Clear();

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
        }
        catch
        {
            // Ignore autofill errors
        }
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _authorizationRequestId++;
        SetAuthorizationVisualState(false);
        var provider = (ProviderComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
        SelectedProvider = provider;

        if (RepositoryComboBox is not null)
        {
            RepositoryComboBox.Items.Clear();
            RepositoryComboBox.Text = string.Empty;
        }
        if (BranchComboBox is not null)
        {
            BranchComboBox.Items.Clear();
            BranchComboBox.Text = string.Empty;
        }
        _fetchedRepositories.Clear();
        _fetchedBranches.Clear();

        if (provider == "git")
        {
            if (TokenSectionStackPanel is not null) TokenSectionStackPanel.Visibility = Visibility.Collapsed;
            if (HostedRepositoryStackPanel is not null) HostedRepositoryStackPanel.Visibility = Visibility.Collapsed;
            if (CustomGitRemoteStackPanel is not null) CustomGitRemoteStackPanel.Visibility = Visibility.Visible;
            _genericGitBaseLocation = LocationTextBox?.Text?.Trim() ?? string.Empty;
            _lastSuggestedGitLocation = string.Empty;
            if (BranchComboBox is not null)
            {
                BranchComboBox.Items.Add("main");
                BranchComboBox.SelectedIndex = 0;
            }
            _isAuthorized = true;
            ValidateInputs();
        }
        else
        {
            if (TokenSectionStackPanel is not null) TokenSectionStackPanel.Visibility = Visibility.Visible;
            if (HostedRepositoryStackPanel is not null) HostedRepositoryStackPanel.Visibility = Visibility.Visible;
            if (CustomGitRemoteStackPanel is not null) CustomGitRemoteStackPanel.Visibility = Visibility.Collapsed;
            UpdateInfoTooltips();
            AutoFillSavedToken();
        }
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
            // Ignore shell errors
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
    }

    private async void OnAuthorizeClick(object sender, RoutedEventArgs e)
    {
        var requestId = 0;
        try
        {
            var token = TokenPasswordBox.Password?.Trim() ?? string.Empty;
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
            var (success, primaryUser, _, error) =
                await _sourceControlService.AuthorizeTokenAsync(provider, token);
            if (requestId != _authorizationRequestId) return;

            if (success)
            {
                _currentToken = token;
                _isAuthorized = true;

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

                await LoadRepositoriesAsync();
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

        _currentToken = string.Empty;
        _isAuthorized = false;
        IsPrimaryButtonEnabled = false;

        TokenPasswordBox.Password = string.Empty;
        RepositoryComboBox.Items.Clear();
        BranchComboBox.Items.Clear();
        _fetchedRepositories.Clear();
        _fetchedBranches.Clear();

        if (TokenLinkedPanel is not null) TokenLinkedPanel.Visibility = Visibility.Collapsed;
        if (TokenUnlinkedPanel is not null) TokenUnlinkedPanel.Visibility = Visibility.Visible;
    }

    private async Task LoadRepositoriesAsync()
    {
        try
        {
            RepositoryComboBox.Items.Clear();
            _fetchedRepositories.Clear();

            var (success, repos, error) = await _sourceControlService.GetRepositoriesAsync(SelectedProvider, _currentToken);
            if (success)
            {
                _fetchedRepositories.AddRange(repos);
                foreach (var repo in repos)
                {
                    RepositoryComboBox.Items.Add(repo.FullName);
                }
                HideStatus();
            }
            else
            {
                ShowStatus($"Failed to fetch repositories: {error}", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Error loading repositories: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnRepositorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            BranchComboBox.Items.Clear();
            _fetchedBranches.Clear();

            var selectedRepoName = RepositoryComboBox.SelectedItem as string
                ?? RepositoryComboBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(selectedRepoName))
            {
                ValidateInputs();
                return;
            }

            var repoItem = _fetchedRepositories.FirstOrDefault(r => string.Equals(r.FullName, selectedRepoName, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, selectedRepoName, StringComparison.OrdinalIgnoreCase));
            var defaultBranch = repoItem?.DefaultBranch ?? "main";

            if (repoItem is not null && !string.IsNullOrWhiteSpace(repoItem.Name))
            {
                var currentPath = LocationTextBox.Text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    var parentDir = Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        LocationTextBox.Text = Path.Combine(parentDir, repoItem.Name);
                    }
                    else
                    {
                        LocationTextBox.Text = Path.Combine(currentPath, repoItem.Name);
                    }
                }
            }

            if (_isAuthorized && !string.IsNullOrWhiteSpace(_currentToken))
            {
                var (success, branches, _) = await _sourceControlService.GetBranchesAsync(SelectedProvider, _currentToken, selectedRepoName, defaultBranch);
                if (success && branches.Count > 0)
                {
                    _fetchedBranches.AddRange(branches);
                    foreach (var branch in branches)
                    {
                        BranchComboBox.Items.Add(branch.Name);
                    }
                    BranchComboBox.SelectedIndex = 0;
                }
            }

            if (BranchComboBox.Items.Count == 0)
            {
                BranchComboBox.Items.Add(defaultBranch);
                BranchComboBox.SelectedIndex = 0;
            }

            ValidateInputs();
        }
        catch
        {
            ValidateInputs();
        }
    }

    private void OnBranchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ValidateInputs();
    }

    private void OnLocationTextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateInputs();
    }

    private void OnCustomGitRemoteTextChanged(object sender, TextChangedEventArgs e)
    {
        var remoteUrl = CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty;
        var repositoryName = GetRepositoryNameFromRemote(remoteUrl);
        var currentLocation = LocationTextBox?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(repositoryName)
            && !string.IsNullOrWhiteSpace(_genericGitBaseLocation)
            && LocationTextBox is not null
            && (string.Equals(currentLocation, _genericGitBaseLocation, StringComparison.OrdinalIgnoreCase)
                || string.Equals(currentLocation, _lastSuggestedGitLocation, StringComparison.OrdinalIgnoreCase)))
        {
            _lastSuggestedGitLocation = Path.Combine(_genericGitBaseLocation, repositoryName);
            LocationTextBox.Text = _lastSuggestedGitLocation;
        }

        ValidateInputs();
    }

    private static string? GetRepositoryNameFromRemote(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var normalized = remoteUrl.TrimEnd('/');
        var separator = Math.Max(normalized.LastIndexOf('/'), normalized.LastIndexOf(':'));
        var name = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private async void OnBrowseLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (XamlRoot is null) return;
            var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Select download location",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
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
            ShowStatus($"Folder picker error: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ValidateInputs()
    {
        var repoSelected = SelectedProvider == "git"
            ? GitService.IsValidRemoteUrl(CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty)
            : !string.IsNullOrWhiteSpace(RepositoryComboBox.SelectedItem as string ?? RepositoryComboBox.Text);
        var branchSelected = !string.IsNullOrWhiteSpace(BranchComboBox.SelectedItem as string ?? BranchComboBox.Text);
        var locationEntered = !string.IsNullOrWhiteSpace(LocationTextBox.Text);

        IsPrimaryButtonEnabled = _isAuthorized && repoSelected && branchSelected && locationEntered;
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

            var selectedRepoText = SelectedProvider == "git"
                ? CustomGitRemoteTextBox?.Text?.Trim() ?? string.Empty
                : RepositoryComboBox.SelectedItem as string ?? RepositoryComboBox.Text?.Trim() ?? string.Empty;
            var selectedBranch = BranchComboBox.SelectedItem as string ?? BranchComboBox.Text?.Trim() ?? "main";
            var targetLocation = LocationTextBox.Text?.Trim() ?? string.Empty;

            var repoItem = _fetchedRepositories.FirstOrDefault(r => string.Equals(r.FullName, selectedRepoText, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, selectedRepoText, StringComparison.OrdinalIgnoreCase));
            string cloneUrl = SelectedProvider == "git"
                ? selectedRepoText
                : repoItem?.CloneUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(cloneUrl))
            {
                if (SelectedProvider == "github")
                {
                    cloneUrl = $"https://github.com/{selectedRepoText}.git";
                }
                else if (SelectedProvider == "gitlab")
                {
                    cloneUrl = $"https://gitlab.com/{selectedRepoText}.git";
                }
            }

            ShowStatus($"Cloning repository ({selectedBranch})... Please wait.", InfoBarSeverity.Informational);

            var (cloneSuccess, cloneMsg) = await GitService.CloneRepositoryAsync(cloneUrl, targetLocation, selectedBranch);
            if (!cloneSuccess)
            {
                args.Cancel = true;
                ShowStatus($"Git clone failed: {cloneMsg}", InfoBarSeverity.Error);
                return;
            }

            var projectTitle = Path.GetFileName(targetLocation);
            var version = UnityHubProjectService.ParseProjectVersion(targetLocation);
            var projectService = new UnityHubProjectService();
            projectService.AddOrUpdateProject(targetLocation, projectTitle, version);

            DownloadedProjectPath = targetLocation;
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            ShowStatus($"Error adding project from repository: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }
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
