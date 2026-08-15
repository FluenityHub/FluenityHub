using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace FluenityHub_WinUIHost.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly SourceControlService _sourceControlService = new();
    private readonly UnityCliToolService _unityCliToolService = new();
    private readonly UnityHubTemplateSettingsService _templateSettingsService = new();
    private readonly UnityHubLocationSettingsService _unityHubLocationSettingsService = new();
    private readonly UnityDiagnosticLocationService _unityDiagnosticLocationService = new();
    private readonly UnityHubProjectSettingsService _unityHubProjectSettingsService = new();
    private readonly UnityHubProjectService _unityHubProjectService = new();
    private AppSettings _settings = new();
    private bool _isInitializing = true;
    private bool _isUnityCliBusy;
    private CancellationTokenSource? _unityCliOperationCancellation;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _isInitializing = true;
        try
        {
            _settings = _settingsStore.Load();

            if (AppThemeComboBox is not null)
            {
                SelectComboBoxByTag(AppThemeComboBox, _settings.AppTheme);
            }

            if (MinimizeBehaviorComboBox is not null)
            {
                MinimizeBehaviorComboBox.SelectedIndex = Math.Clamp(_settings.MinimizeBehavior, 0, 3);
            }

            if (LowerPriorityToggleSwitch is not null)
            {
                LowerPriorityToggleSwitch.IsOn = _settings.LowerPriorityWhenUnityOpens;
            }

            if (ExplorerContextMenuToggleSwitch is not null)
            {
                ExplorerContextMenuToggleSwitch.IsOn = _settings.ExplorerContextMenuEnabled;
            }

            if (EnableSourceControlToggleSwitch is not null)
            {
                EnableSourceControlToggleSwitch.IsOn = _settings.EnableSourceControl;
            }
            if (ClearSourceControlTokensOnLogoutToggleSwitch is not null)
            {
                ClearSourceControlTokensOnLogoutToggleSwitch.IsOn =
                    _unityHubProjectSettingsService.GetClearTokensOnLogout();
            }
            UpdateSourceControlCardsVisibility(_settings.EnableSourceControl);

            var ghToken = CredentialService.GetGitHubToken();
            if (GitHubTokenPasswordBox is not null) GitHubTokenPasswordBox.Password = ghToken;
            UpdateGitHubTokenUI(ghToken);

            var glToken = CredentialService.GetGitLabToken();
            if (GitLabTokenPasswordBox is not null) GitLabTokenPasswordBox.Password = glToken;
            UpdateGitLabTokenUI(glToken);

            if (_settings.EnableSourceControl)
            {
                _ = VerifySavedTokensAsync(ghToken, glToken);
            }

            RefreshUnityCliStatus();
        }
        catch (Exception ex)
        {
            _settings = new AppSettings();
            ShowStatus($"Settings loaded with defaults: {ex.Message}", InfoBarSeverity.Warning);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || AppThemeComboBox is null)
        {
            return;
        }

        var themeTag = (AppThemeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        _settings.AppTheme = themeTag;

        SaveSettings();
        MainWindow.Instance?.SetAppTheme(themeTag);
    }

    private void OnOpenUnityLicensesClick(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(
            typeof(LicensesPage),
            null,
            new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
    }

    private void OnOpenUnityLocationsClick(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(
            typeof(LocationsPage),
            null,
            new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromRight
            });
    }

    private void OnMinimizeBehaviorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || MinimizeBehaviorComboBox is null)
        {
            return;
        }

        var minimizeIndex = MinimizeBehaviorComboBox.SelectedIndex >= 0 ? MinimizeBehaviorComboBox.SelectedIndex : 0;
        _settings.MinimizeBehavior = minimizeIndex;

        SaveSettings();
    }

    private void OnLowerPriorityToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || LowerPriorityToggleSwitch is null)
        {
            return;
        }

        _settings.LowerPriorityWhenUnityOpens = LowerPriorityToggleSwitch.IsOn;

        SaveSettings();
        MainWindow.Instance?.EvaluateProcessPriority();
    }

    private void OnExplorerContextMenuToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ExplorerContextMenuToggleSwitch is null)
        {
            return;
        }

        try
        {
            var enabled = ExplorerContextMenuToggleSwitch.IsOn;
            ShellIntegrationService.SetExplorerContextMenuEnabled(enabled);
            _settings.ExplorerContextMenuEnabled = enabled;
            SaveSettings();
            ShowStatus(
                enabled
                    ? "File Explorer integration enabled."
                    : "File Explorer integration disabled.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _isInitializing = true;
            ExplorerContextMenuToggleSwitch.IsOn = _settings.ExplorerContextMenuEnabled;
            _isInitializing = false;
            ShowStatus($"File Explorer integration could not be updated: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnEnableSourceControlToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        bool enabled = false;
        if (sender is ToggleSwitch ts)
        {
            enabled = ts.IsOn;
        }

        _settings.EnableSourceControl = enabled;

        SaveSettings();
        UpdateSourceControlCardsVisibility(enabled);

        if (enabled)
        {
            _ = VerifySavedTokensAsync(
                CredentialService.GetGitHubToken(),
                CredentialService.GetGitLabToken());
        }
    }

    private void OnClearSourceControlTokensOnLogoutToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing
            || sender is not ToggleSwitch toggleSwitch)
        {
            return;
        }

        try
        {
            _unityHubProjectSettingsService.SetClearTokensOnLogout(toggleSwitch.IsOn);
        }
        catch (Exception ex)
        {
            _isInitializing = true;
            toggleSwitch.IsOn = _unityHubProjectSettingsService.GetClearTokensOnLogout();
            _isInitializing = false;
            ShowStatus(
                $"Unable to update the token cleanup preference: {ex.Message}",
                InfoBarSeverity.Error);
        }
    }

    private void UpdateSourceControlCardsVisibility(bool enabled)
    {
        _isInitializing = true;
        try
        {
            if (EnableSourceControlToggleSwitch is not null) EnableSourceControlToggleSwitch.IsOn = enabled;

            if (SourceControlSettingsExpander is not null)
            {
                SourceControlSettingsExpander.IsExpanded = enabled;
            }

            if (GitHubCard is not null) GitHubCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (GitLabCard is not null) GitLabCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (SourceControlLocalStorageCard is not null)
            {
                SourceControlLocalStorageCard.Visibility = enabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OnGetGitHubTokenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/settings/tokens/new?description=FluenityHub&scopes=repo",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore
        }
    }

    private void OnGetGitLabTokenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://gitlab.com/-/user_settings/personal_access_tokens?name=FluenityHub&scopes=api,read_user",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore
        }
    }

    private void OnGitHubTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (GitHubAuthorizeButton is not null && GitHubTokenPasswordBox is not null)
        {
            GitHubAuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(GitHubTokenPasswordBox.Password);
        }
    }

    private void OnGitLabTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (GitLabAuthorizeButton is not null && GitLabTokenPasswordBox is not null)
        {
            GitLabAuthorizeButton.IsEnabled = !string.IsNullOrWhiteSpace(GitLabTokenPasswordBox.Password);
        }
    }

    private async void OnSaveGitHubTokenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isInitializing || GitHubTokenPasswordBox is null) return;

            var token = GitHubTokenPasswordBox.Password?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                OnGetGitHubTokenClick(sender, e);
                ShowStatus("Opened GitHub token creation page in browser. Paste your token here and click Authorize.", InfoBarSeverity.Success);
                return;
            }

            if (GitHubAuthorizeButton is not null) GitHubAuthorizeButton.IsEnabled = false;
            if (GitHubAuthorizeProgressRing is not null)
            {
                GitHubAuthorizeProgressRing.IsActive = true;
                GitHubAuthorizeProgressRing.Visibility = Visibility.Visible;
            }
            if (GitHubAuthorizeTextBlock is not null) GitHubAuthorizeTextBlock.Text = "Authorizing...";

            var (success, primaryUser, _, errorMessage) = await _sourceControlService.AuthorizeTokenAsync("github", token);

            if (success)
            {
                CredentialService.SaveGitHubToken(token);
                UpdateGitHubTokenUI(token, primaryUser);
                ShowStatus($"GitHub authorization successful for '@{primaryUser}'! Saved securely to Windows Credential Manager.", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus($"GitHub authorization failed: {errorMessage}", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"GitHub authorization error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (GitHubAuthorizeButton is not null) GitHubAuthorizeButton.IsEnabled = true;
            if (GitHubAuthorizeProgressRing is not null)
            {
                GitHubAuthorizeProgressRing.IsActive = false;
                GitHubAuthorizeProgressRing.Visibility = Visibility.Collapsed;
            }
            if (GitHubAuthorizeTextBlock is not null) GitHubAuthorizeTextBlock.Text = "Authorize";
        }
    }

    private async void OnSaveGitLabTokenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isInitializing || GitLabTokenPasswordBox is null) return;

            var token = GitLabTokenPasswordBox.Password?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                OnGetGitLabTokenClick(sender, e);
                ShowStatus("Opened GitLab token creation page in browser. Paste your token here and click Authorize.", InfoBarSeverity.Success);
                return;
            }

            if (GitLabAuthorizeButton is not null) GitLabAuthorizeButton.IsEnabled = false;
            if (GitLabAuthorizeProgressRing is not null)
            {
                GitLabAuthorizeProgressRing.IsActive = true;
                GitLabAuthorizeProgressRing.Visibility = Visibility.Visible;
            }
            if (GitLabAuthorizeTextBlock is not null) GitLabAuthorizeTextBlock.Text = "Authorizing...";

            var (success, primaryUser, _, errorMessage) = await _sourceControlService.AuthorizeTokenAsync("gitlab", token);

            if (success)
            {
                CredentialService.SaveGitLabToken(token);
                UpdateGitLabTokenUI(token, primaryUser);
                ShowStatus($"GitLab authorization successful for '@{primaryUser}'! Saved securely to Windows Credential Manager.", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus($"GitLab authorization failed: {errorMessage}", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"GitLab authorization error: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (GitLabAuthorizeButton is not null) GitLabAuthorizeButton.IsEnabled = true;
            if (GitLabAuthorizeProgressRing is not null)
            {
                GitLabAuthorizeProgressRing.IsActive = false;
                GitLabAuthorizeProgressRing.Visibility = Visibility.Collapsed;
            }
            if (GitLabAuthorizeTextBlock is not null) GitLabAuthorizeTextBlock.Text = "Authorize";
        }
    }

    private void OnDisconnectGitHubTokenClick(object sender, RoutedEventArgs e)
    {
        CredentialService.RemoveGitHubToken();
        if (GitHubTokenPasswordBox is not null) GitHubTokenPasswordBox.Password = string.Empty;
        UpdateGitHubTokenUI(string.Empty);
        ShowStatus("GitHub token disconnected.", InfoBarSeverity.Success);
    }

    private void OnDisconnectGitLabTokenClick(object sender, RoutedEventArgs e)
    {
        CredentialService.RemoveGitLabToken();
        if (GitLabTokenPasswordBox is not null) GitLabTokenPasswordBox.Password = string.Empty;
        UpdateGitLabTokenUI(string.Empty);
        ShowStatus("GitLab token disconnected.", InfoBarSeverity.Success);
    }

    private void UpdateGitHubTokenUI(string token, string? username = null)
    {
        bool hasToken = !string.IsNullOrWhiteSpace(token);
        if (GitHubUnlinkedPanel is not null) GitHubUnlinkedPanel.Visibility = hasToken ? Visibility.Collapsed : Visibility.Visible;
        if (GitHubLinkedPanel is not null) GitHubLinkedPanel.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
        if (GitHubUserTextBlock is not null && hasToken)
        {
            GitHubUserTextBlock.Text = !string.IsNullOrWhiteSpace(username) ? $"Connected as @{username}" : "Connected";
        }
    }

    private void UpdateGitLabTokenUI(string token, string? username = null)
    {
        bool hasToken = !string.IsNullOrWhiteSpace(token);
        if (GitLabUnlinkedPanel is not null) GitLabUnlinkedPanel.Visibility = hasToken ? Visibility.Collapsed : Visibility.Visible;
        if (GitLabLinkedPanel is not null) GitLabLinkedPanel.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
        if (GitLabUserTextBlock is not null && hasToken)
        {
            GitLabUserTextBlock.Text = !string.IsNullOrWhiteSpace(username) ? $"Connected as @{username}" : "Connected";
        }
    }

    private async Task VerifySavedTokensAsync(string ghToken, string glToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                var (success, primaryUser, _, _) = await _sourceControlService.AuthorizeTokenAsync("github", ghToken);
                if (success && !string.IsNullOrWhiteSpace(primaryUser))
                {
                    UpdateGitHubTokenUI(ghToken, primaryUser);
                }
            }
            if (!string.IsNullOrWhiteSpace(glToken))
            {
                var (success, primaryUser, _, _) = await _sourceControlService.AuthorizeTokenAsync("gitlab", glToken);
                if (success && !string.IsNullOrWhiteSpace(primaryUser))
                {
                    UpdateGitLabTokenUI(glToken, primaryUser);
                }
            }
        }
        catch
        {
            // Ignore background verification errors
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            ShowStatus("Settings saved.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to save settings: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private static void SelectComboBoxByTag(ComboBox comboBox, string tagValue)
    {
        foreach (ComboBoxItem item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tagValue)
            {
                comboBox.SelectedItem = item;
                break;
            }
        }
    }

    private CancellationTokenSource? _infoBarCts;
    private CancellationTokenSource? _unityCliInfoBarCts;

    private void OnCheckUnityHubClick(object sender, RoutedEventArgs e)
    {
        var projectsJsonPath = _unityDiagnosticLocationService.GetUnityHubDataFilePath(
            "projects-v1.json");
        var sharedFiles = new (string Label, string Path)[]
        {
            ("project list", projectsJsonPath),
            ("project location", UnityHubProjectSettingsService.ProjectLocationFilePath),
            ("project naming", UnityHubProjectSettingsService.UserSettingsFilePath),
            ("templates", UnityHubTemplateSettingsService.SettingsFilePath),
            ("install location", UnityHubLocationSettingsService.InstallLocationFilePath),
            ("download location", UnityHubLocationSettingsService.DownloadLocationFilePath)
        };
        var missingSettings = sharedFiles
            .Where(item => !File.Exists(item.Path))
            .Select(item => item.Label)
            .ToList();
        var projectNameMode = _unityHubProjectSettingsService.GetShowProductNames()
            ? "Product name"
            : "Folder name";
        var tokenStorageMode = _unityHubProjectSettingsService.GetClearTokensOnLogout()
            ? "clear tokens on logout"
            : "keep tokens on this device";
        var diagnosticLocations = _unityDiagnosticLocationService.ResolveAll();
        var availableDiagnosticLocations = diagnosticLocations.Count(location => location.Exists);
        var summary =
            $"Projects: {_unityHubProjectSettingsService.GetProjectLocation()} · " +
            $"Templates: {_templateSettingsService.GetCurrentPath()} · " +
            $"Editors: {_unityHubLocationSettingsService.GetInstallLocation()} · " +
            $"Downloads: {_unityHubLocationSettingsService.GetDownloadLocation()} · " +
            $"Names: {projectNameMode} · " +
            $"Security: {tokenStorageMode} · " +
            $"Diagnostics: {availableDiagnosticLocations}/{diagnosticLocations.Count} locations available";

        ShowUnityHubStatus(
            missingSettings.Count == 0
                ? $"Unity Hub shared data is connected. {summary}"
                : $"Unity Hub shared data is connected with defaults for: {string.Join(", ", missingSettings)}. {summary}",
            missingSettings.Count == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Success);
    }

    private async void ShowUnityHubStatus(string message, InfoBarSeverity severity)
    {
        _infoBarCts?.Cancel();
        _infoBarCts = new CancellationTokenSource();
        var token = _infoBarCts.Token;

        UnityHubStatusInfoBar.Message = message;
        UnityHubStatusInfoBar.Severity = severity;
        UnityHubStatusInfoBar.Visibility = Visibility.Visible;
        UnityHubStatusInfoBar.IsOpen = true;

        try
        {
            await Task.Delay(4000, token);
            if (!token.IsCancellationRequested)
            {
                UnityHubStatusInfoBar.IsOpen = false;
                UnityHubStatusInfoBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (TaskCanceledException)
        {
            // Reset by subsequent call
        }
    }

    private void OnUnityHubStatusInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }

    private void RefreshUnityCliStatus()
    {
        var status = _unityCliToolService.GetStatus();
        if (status.IsInstalled)
        {
            UnityCliDescriptionTextBlock.Text =
                $"Unity CLI {status.Version} · {UnityCliToolService.ReleaseChannelDisplayName} channel · Managed by FluenityHub.";
            UnityCliActionTextBlock.Text = "Check for updates";
            UnityCliMoreButton.Visibility = Visibility.Visible;
        }
        else
        {
            UnityCliDescriptionTextBlock.Text =
                "Install Unity's standalone CLI on demand to manage Editor modules without opening Unity Hub.";
            UnityCliActionTextBlock.Text = "Install";
            UnityCliMoreButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnUnityCliActionClick(object sender, RoutedEventArgs e)
    {
        if (_isUnityCliBusy)
        {
            return;
        }

        SetUnityCliBusy(true);
        _unityCliOperationCancellation?.Cancel();
        _unityCliOperationCancellation?.Dispose();
        var operationCancellation = new CancellationTokenSource();
        _unityCliOperationCancellation = operationCancellation;
        var cancellationToken = operationCancellation.Token;
        try
        {
            SetUnityCliOperationStatus("Checking the latest release…");
            UnityCliActionTextBlock.Text = "Checking…";

            var release = await _unityCliToolService.GetLatestReleaseAsync(cancellationToken);
            var current = _unityCliToolService.GetStatus();
            if (current.IsInstalled
                && !UnityCliToolService.IsReleaseNewer(current.Version, release.Version))
            {
                var statusMessage = current.Version?.Equals(
                    release.Version,
                    StringComparison.OrdinalIgnoreCase) == true
                        ? $"Unity CLI {release.Version} is the latest {UnityCliToolService.ReleaseChannelDisplayName} release."
                        : $"Unity CLI {current.Version} is newer than the latest published {UnityCliToolService.ReleaseChannelDisplayName} release ({release.Version}).";
                ShowUnityCliStatus(statusMessage, InfoBarSeverity.Success);
                return;
            }

            var verb = current.IsInstalled ? "Update" : "Install";
            SetUnityCliOperationStatus($"{verb} available: Unity CLI {release.Version}");
            if (!await ConfirmUnityCliInstallAsync(release, verb))
            {
                return;
            }

            var operationText = verb.Equals("Update", StringComparison.Ordinal)
                ? "Updating"
                : "Installing";
            UnityCliActionTextBlock.Text = $"{operationText}…";
            SetUnityCliOperationStatus($"{operationText} Unity CLI…");
            var progress = new Progress<UnityCliDownloadProgress>(value =>
            {
                SetUnityCliOperationStatus(value.TotalBytes is > 0
                    ? $"{operationText} Unity CLI… {FormatToolSize(value.BytesReceived)} of {FormatToolSize(value.TotalBytes.Value)}"
                    : $"{operationText} Unity CLI… {FormatToolSize(value.BytesReceived)}");
            });

            var installed = await _unityCliToolService.InstallAsync(
                release,
                progress,
                cancellationToken);
            RefreshUnityCliStatus();
            ShowUnityCliStatus(
                $"Unity CLI {installed.Version} is ready. Module operations will not launch Unity Hub.",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowUnityCliStatus(
                $"Unable to install Unity CLI: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_unityCliOperationCancellation, operationCancellation))
            {
                _unityCliOperationCancellation = null;
            }

            operationCancellation.Dispose();
            SetUnityCliBusy(false);
        }
    }

    private async void OnRemoveUnityCliClick(object sender, RoutedEventArgs e)
    {
        if (_isUnityCliBusy || XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Remove Unity CLI?",
            Content = "This removes the optional command-line tool managed by FluenityHub. Unity Editors and installed modules are not removed.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            SetUnityCliBusy(true);
            _unityCliToolService.Remove();
            RefreshUnityCliStatus();
            ShowUnityCliStatus("Unity CLI was removed.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowUnityCliStatus(
                $"Unable to remove Unity CLI: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            SetUnityCliBusy(false);
        }
    }

    private void OnOpenUnityCliDocumentationClick(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://docs.unity.com/en-us/unity-cli/use-unity-cli");
    }

    private async Task<bool> ConfirmUnityCliInstallAsync(
        UnityCliReleaseInfo release,
        string verb)
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var details = new StackPanel { Spacing = 8 };
        details.Children.Add(new TextBlock
        {
            Text = $"Unity CLI {release.Version} will be downloaded directly from Unity.",
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new TextBlock
        {
            Text = release.DownloadSizeBytes is > 0
                ? $"Download size: {FormatToolSize(release.DownloadSizeBytes.Value)}"
                : "The download size will be shown when the transfer starts."
        });
        details.Children.Add(new TextBlock
        {
            Text = $"Storage location: {UnityCliToolService.ToolRootPath}",
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(new HyperlinkButton
        {
            Content = "Unity CLI documentation",
            NavigateUri = new Uri("https://docs.unity.com/en-us/unity-cli/use-unity-cli"),
            Padding = new Thickness(0)
        });

        var dialog = new ContentDialog
        {
            Title = $"{verb} Unity CLI?",
            Content = details,
            PrimaryButtonText = verb,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetUnityCliBusy(bool isBusy)
    {
        _isUnityCliBusy = isBusy;
        UnityCliActionButton.IsEnabled = !isBusy;
        UnityCliMoreButton.IsEnabled = !isBusy;
        UnityCliRemoveMenuItem.IsEnabled = !isBusy;
        UnityCliActionProgressRing.IsActive = isBusy;
        UnityCliActionProgressRing.Visibility = isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!isBusy)
        {
            RefreshUnityCliStatus();
        }
    }

    private void SetUnityCliOperationStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        UnityCliStatusInfoBar.Message = message;
        UnityCliStatusInfoBar.Severity = severity;
        UnityCliStatusInfoBar.Visibility = Visibility.Visible;
        UnityCliStatusInfoBar.IsOpen = true;
    }

    private async void ShowUnityCliStatus(
        string message,
        InfoBarSeverity severity)
    {
        _unityCliInfoBarCts?.Cancel();
        _unityCliInfoBarCts?.Dispose();
        _unityCliInfoBarCts = new CancellationTokenSource();
        var token = _unityCliInfoBarCts.Token;

        UnityCliStatusInfoBar.Message = message;
        UnityCliStatusInfoBar.Severity = severity;
        UnityCliStatusInfoBar.Visibility = Visibility.Visible;
        UnityCliStatusInfoBar.IsOpen = true;

        try
        {
            await Task.Delay(4000, token);
            if (!token.IsCancellationRequested)
            {
                UnityCliStatusInfoBar.IsOpen = false;
                UnityCliStatusInfoBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnUnityCliStatusInfoBarClosed(
        InfoBar sender,
        InfoBarClosedEventArgs args)
    {
        sender.Visibility = Visibility.Collapsed;
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _unityCliOperationCancellation?.Cancel();
        _unityCliOperationCancellation?.Dispose();
        _unityCliOperationCancellation = null;
        _unityCliInfoBarCts?.Cancel();
        _unityCliInfoBarCts?.Dispose();
        _unityCliInfoBarCts = null;
    }

    private static string FormatToolSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 bytes";
        }

        string[] units = ["bytes", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    private async void OnRemoveMissingProjectsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (XamlRoot is null) return;

            var dialog = new ContentDialog
            {
                Title = "Remove missing projects?",
                Content = "This will remove projects from the Hub that are no longer found at their specified location. This action cannot be undone.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var service = new UnityHubProjectService();
                int removedCount = service.RemoveMissingProjects();

                if (RemoveMissingStatusInfoBar is not null)
                {
                    if (removedCount > 0)
                    {
                        RemoveMissingStatusInfoBar.Severity = InfoBarSeverity.Success;
                        RemoveMissingStatusInfoBar.Title = "Projects cleaned";
                        RemoveMissingStatusInfoBar.Message = $"Successfully removed {removedCount} missing project(s) from FluenityHub.";
                    }
                    else
                    {
                        RemoveMissingStatusInfoBar.Severity = InfoBarSeverity.Success;
                        RemoveMissingStatusInfoBar.Title = "No missing projects";
                        RemoveMissingStatusInfoBar.Message = "All registered projects were found on your computer.";
                    }
                    RemoveMissingStatusInfoBar.IsOpen = true;
                    RemoveMissingStatusInfoBar.Visibility = Visibility.Visible;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] Error removing missing projects: {ex}");
        }
    }

    private void OnRemoveMissingStatusInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (RemoveMissingStatusInfoBar is not null)
        {
            RemoveMissingStatusInfoBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenDiagnosticLocationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string locationKey })
        {
            return;
        }

        var diagnosticLocation = _unityDiagnosticLocationService.Resolve(locationKey);
        if (diagnosticLocation is null)
        {
            return;
        }

        if (!diagnosticLocation.Exists)
        {
            ShowStatus(
                $"This location has not been created yet: {diagnosticLocation.Path}",
                InfoBarSeverity.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = diagnosticLocation.Path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to open this location: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnOpenDocsClick(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://learn.microsoft.com/windows/apps/winui/winui3/");
    }

    private void OnOpenUnityWebsiteClick(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        OpenUrl("https://unity.com/");
    }

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/FluenityHub/FluenityHub/issues/new/choose");
    }

    private void OnOpenCommunityToolkitClick(object sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/CommunityToolkit/Windows");
    }

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to open browser: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private CancellationTokenSource? _statusInfoBarCts;

    private async void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            return;
        }

        try
        {
            if (StatusInfoBar is null) return;

            _statusInfoBarCts?.Cancel();
            _statusInfoBarCts = new CancellationTokenSource();
            var token = _statusInfoBarCts.Token;

            StatusInfoBar.Title = severity switch
            {
                InfoBarSeverity.Error => "Error",
                InfoBarSeverity.Warning => "Warning",
                InfoBarSeverity.Success => "Success",
                _ => "Info"
            };
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;
            StatusInfoBar.Visibility = Visibility.Visible;

            try
            {
                await Task.Delay(4000, token);
                if (!token.IsCancellationRequested)
                {
                    StatusInfoBar.IsOpen = false;
                    StatusInfoBar.Visibility = Visibility.Collapsed;
                }
            }
            catch (TaskCanceledException)
            {
                // Reset by subsequent call
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] ShowStatus failed: {ex}");
        }
    }

    private void OnStatusInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (StatusInfoBar is not null)
        {
            StatusInfoBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUnityDiscussionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://discussions.unity.com/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Unity Discussions: {ex.Message}");
        }
    }

    private void OnUnityManualClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://docs.unity3d.com/Manual/index.html") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Unity Manual: {ex.Message}");
        }
    }

    private void OnUnityLearnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://learn.unity.com/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Unity Learn: {ex.Message}");
        }
    }

    private AppUpdateInfo? _settingsUpdateInfo;

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CheckUpdatesButton is not null) CheckUpdatesButton.IsEnabled = false;
            if (CheckUpdatesButtonTextBlock is not null) CheckUpdatesButtonTextBlock.Text = "Checking...";
            if (CheckUpdatesProgressRing is not null)
            {
                CheckUpdatesProgressRing.IsActive = true;
                CheckUpdatesProgressRing.Visibility = Visibility.Visible;
            }

            var updateInfo = await AppUpdateService.CheckForUpdatesAsync();
            _settingsUpdateInfo = updateInfo;

            if (SettingsUpdateInfoBar is not null)
            {
                if (updateInfo.HasUpdate)
                {
                    SettingsUpdateInfoBar.Severity = InfoBarSeverity.Success;
                    SettingsUpdateInfoBar.Title = $"FluenityHub v{updateInfo.LatestVersion} is available";
                    SettingsUpdateInfoBar.Message = string.IsNullOrWhiteSpace(updateInfo.ReleaseTitle)
                        ? "A new version of FluenityHub is available with new features and performance improvements."
                        : updateInfo.ReleaseTitle;

                    if (SettingsInstallUpdateBtn is not null) SettingsInstallUpdateBtn.Visibility = Visibility.Visible;
                    if (SettingsSeeChangesBtn is not null) SettingsSeeChangesBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    SettingsUpdateInfoBar.Severity = InfoBarSeverity.Success;
                    SettingsUpdateInfoBar.Title = "FluenityHub is up to date";
                    SettingsUpdateInfoBar.Message = $"You are running the latest version (v{AppUpdateService.CurrentVersion}).";

                    if (SettingsInstallUpdateBtn is not null) SettingsInstallUpdateBtn.Visibility = Visibility.Collapsed;
                    if (SettingsSeeChangesBtn is not null) SettingsSeeChangesBtn.Visibility = Visibility.Collapsed;
                }

                SettingsUpdateInfoBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            if (SettingsUpdateInfoBar is not null)
            {
                SettingsUpdateInfoBar.Severity = InfoBarSeverity.Error;
                SettingsUpdateInfoBar.Title = "Unable to check for updates";
                SettingsUpdateInfoBar.Message = ex.Message;
                if (SettingsInstallUpdateBtn is not null) SettingsInstallUpdateBtn.Visibility = Visibility.Collapsed;
                if (SettingsSeeChangesBtn is not null) SettingsSeeChangesBtn.Visibility = Visibility.Collapsed;
                SettingsUpdateInfoBar.IsOpen = true;
            }
        }
        finally
        {
            if (CheckUpdatesButton is not null) CheckUpdatesButton.IsEnabled = true;
            if (CheckUpdatesButtonTextBlock is not null) CheckUpdatesButtonTextBlock.Text = "Check for updates";
            if (CheckUpdatesProgressRing is not null)
            {
                CheckUpdatesProgressRing.IsActive = false;
                CheckUpdatesProgressRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnSettingsInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        var targetUrl = _settingsUpdateInfo?.DownloadUrl ?? _settingsUpdateInfo?.ReleaseUrl ?? "https://github.com/FluenityHub/FluenityHub/releases";
        OpenUrl(targetUrl);
    }

    private void OnSettingsSeeChangesClick(object sender, RoutedEventArgs e)
    {
        var targetUrl = _settingsUpdateInfo?.ReleaseUrl ?? "https://github.com/FluenityHub/FluenityHub/releases";
        OpenUrl(targetUrl);
    }

    private void OnSettingsUpdateInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (SettingsUpdateInfoBar is not null)
        {
            SettingsUpdateInfoBar.IsOpen = false;
        }
    }
}
