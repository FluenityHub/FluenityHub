using System.Diagnostics;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Pages;

public sealed partial class LocationsPage : Page
{
    private readonly UnityHubProjectSettingsService _unityHubProjectSettingsService = new();
    private readonly UnityHubProjectService _unityHubProjectService = new();
    private readonly UnityHubTemplateSettingsService _templateSettingsService = new();
    private readonly UnityHubLocationSettingsService _unityHubLocationSettingsService = new();
    private readonly AppSettingsStore _settingsStore = new();

    private AppSettings _settings;
    private bool _isInitializing = true;
    private CancellationTokenSource? _statusInfoBarCts;

    public LocationsPage()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        SettingsBreadcrumbBar.ItemsSource = new[] { "Settings", "Locations & search paths" };
        Loaded += OnPageLoaded;
    }

    private void OnSettingsBreadcrumbItemClicked(
        BreadcrumbBar sender,
        BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0 && Frame?.CanGoBack == true)
        {
            Frame.GoBack();
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        try
        {
            _settings = _settingsStore.Load();

            if (UnityHubProjectLocationTextBox is not null)
            {
                UnityHubProjectLocationTextBox.Text =
                    _unityHubProjectSettingsService.GetProjectLocation();
            }

            if (UnityHubTemplatesLocationTextBox is not null)
            {
                UnityHubTemplatesLocationTextBox.Text =
                    _templateSettingsService.GetCurrentPath();
            }

            if (UnityEditorInstallLocationTextBox is not null)
            {
                UnityEditorInstallLocationTextBox.Text =
                    _unityHubLocationSettingsService.GetInstallLocation();
            }

            if (UnityHubDownloadLocationTextBox is not null)
            {
                UnityHubDownloadLocationTextBox.Text =
                    _unityHubLocationSettingsService.GetDownloadLocation();
            }

            if (DefaultProjectNameComboBox is not null)
            {
                DefaultProjectNameComboBox.SelectedIndex =
                    _unityHubProjectSettingsService.GetShowProductNames() ? 1 : 0;
            }

            PopulateCustomEditorPathsUI();
            PopulateCustomTemplatePathsUI();
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to load location settings: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _statusInfoBarCts?.Cancel();
        _statusInfoBarCts?.Dispose();
        _statusInfoBarCts = null;
    }

    private void SaveSettings()
    {
        _settingsStore.Save(_settings);
    }

    private async void OnChooseUnityHubProjectLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Choose where new Unity projects are saved",
                CommitButtonText = "Select folder",
                SuggestedStartLocation =
                    Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var path = Path.GetFullPath(folder.Path);
            _unityHubProjectSettingsService.SetProjectLocation(path);
            UnityHubProjectLocationTextBox.Text = path;
            var importedCount = await Task.Run(
                () => _unityHubProjectService.ImportProjectsFromDirectory(path));
            ShowStatus(
                importedCount == 1
                    ? "Project location updated. 1 Unity project was imported."
                    : $"Project location updated. {importedCount} Unity projects were imported.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to change the Unity Hub project location: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnResetUnityHubProjectLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            var path = UnityHubProjectSettingsService.DefaultProjectLocation;
            _unityHubProjectSettingsService.SetProjectLocation(path);
            UnityHubProjectLocationTextBox.Text = path;
            var importedCount = await Task.Run(
                () => _unityHubProjectService.ImportProjectsFromDirectory(path));
            ShowStatus(
                importedCount == 1
                    ? "Project location reset. 1 Unity project was imported."
                    : $"Project location reset. {importedCount} Unity projects were imported.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to reset the Unity Hub project location: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnChooseUnityHubTemplatesLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Choose where custom Unity templates are saved",
                CommitButtonText = "Select folder",
                SuggestedStartLocation =
                    Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var path = Path.GetFullPath(folder.Path);
            _templateSettingsService.SetCurrentPath(path);
            UnityHubTemplatesLocationTextBox.Text = path;
            ShowStatus(
                "Unity Hub templates location updated.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to change the Unity Hub templates location: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnResetUnityHubTemplatesLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var path = UnityHubTemplateSettingsService.DefaultTemplatesPath;
            _templateSettingsService.SetCurrentPath(path);
            UnityHubTemplatesLocationTextBox.Text = path;
            ShowStatus(
                "Unity Hub templates location reset.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to reset the Unity Hub templates location: {ex.Message}",
                InfoBarSeverity.Error);
        }
    }

    private async void OnChooseUnityEditorInstallLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Choose where Unity Editors and Learn modules are installed",
                CommitButtonText = "Select folder",
                SuggestedStartLocation =
                    Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var path = Path.GetFullPath(folder.Path);
            _unityHubLocationSettingsService.SetInstallLocation(path);
            UnityEditorInstallLocationTextBox.Text = path;
            ShowStatus(
                "Unity Editor install location updated.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to change the Unity Editor install location: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnResetUnityEditorInstallLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var path = UnityHubLocationSettingsService.DefaultInstallLocation;
            _unityHubLocationSettingsService.SetInstallLocation(path);
            UnityEditorInstallLocationTextBox.Text = path;
            ShowStatus(
                "Unity Editor install location reset.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to reset the Unity Editor install location: {ex.Message}",
                InfoBarSeverity.Error);
        }
    }

    private async void OnChooseUnityHubDownloadLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Choose where Unity Hub downloads Editors and Learn content",
                CommitButtonText = "Select folder",
                SuggestedStartLocation =
                    Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var path = Path.GetFullPath(folder.Path);
            _unityHubLocationSettingsService.SetDownloadLocation(path);
            UnityHubDownloadLocationTextBox.Text = path;
            ShowStatus(
                "Unity Hub download location updated.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to change the Unity Hub download location: {ex.Message}",
                InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnResetUnityHubDownloadLocationClick(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var path = UnityHubLocationSettingsService.DefaultDownloadLocation;
            _unityHubLocationSettingsService.SetDownloadLocation(path);
            UnityHubDownloadLocationTextBox.Text = path;
            ShowStatus(
                "Unity Hub download location reset.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(
                $"Unable to reset the Unity Hub download location: {ex.Message}",
                InfoBarSeverity.Error);
        }
    }

    private void OnDefaultProjectNameSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isInitializing || DefaultProjectNameComboBox.SelectedIndex < 0)
        {
            return;
        }

        try
        {
            var showProductNames =
                DefaultProjectNameComboBox.SelectedIndex == 1;
            _unityHubProjectSettingsService.SetShowProductNames(showProductNames);
            ShowStatus(
                showProductNames
                    ? "Projects now use their Unity product name by default."
                    : "Projects now use their folder name by default.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _isInitializing = true;
            DefaultProjectNameComboBox.SelectedIndex =
                _unityHubProjectSettingsService.GetShowProductNames() ? 1 : 0;
            _isInitializing = false;
            ShowStatus(
                $"Unable to change the default project name: {ex.Message}",
                InfoBarSeverity.Error);
        }
    }

    private async void OnAddCustomEditorPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;

            var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Add an Editor search path",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var folderPath = folder.Path;
            var updatedPaths = new List<string>(_settings.CustomEditorPaths ?? []);
            if (!updatedPaths.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            {
                updatedPaths.Add(folderPath);
                _settings.CustomEditorPaths = updatedPaths;
                SaveSettings();
                PopulateCustomEditorPathsUI();
                CustomEditorPathsExpander.IsExpanded = true;
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to add Editor search path: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void PopulateCustomEditorPathsUI()
    {
        if (CustomEditorPathsExpander is null) return;

        var cards = new List<CommunityToolkit.WinUI.Controls.SettingsCard>();

        if (_settings.CustomEditorPaths == null || _settings.CustomEditorPaths.Count == 0)
        {
            var emptyCard = new CommunityToolkit.WinUI.Controls.SettingsCard
            {
                Header = "No custom search paths added",
                Description = "Click 'Add folder' to specify additional Unity Editor search locations.",
                IsEnabled = false
            };
            cards.Add(emptyCard);
        }
        else
        {
            foreach (var path in _settings.CustomEditorPaths)
            {
                var moreButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE712", FontSize = 12 },
                    Style = (Style)Application.Current.Resources["SubtleButtonStyle"]
                };
                ToolTipService.SetToolTip(moreButton, "More options");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(moreButton, $"More options for {path}");

                var flyout = new MenuFlyout();

                var openFolderItem = new MenuFlyoutItem
                {
                    Text = "Open in File Explorer",
                    Icon = new FontIcon { Glyph = "\uE8B7" },
                    Tag = path
                };
                openFolderItem.Click += (s, args) =>
                {
                    if (s is MenuFlyoutItem { Tag: string folderPath })
                    {
                        OpenFolderPath(folderPath);
                    }
                };

                var removeItem = new MenuFlyoutItem
                {
                    Text = "Remove",
                    Icon = new FontIcon { Glyph = "\uE74D" },
                    Tag = path
                };
                removeItem.Click += (s, args) =>
                {
                    if (s is MenuFlyoutItem { Tag: string pathToRemove })
                    {
                        RemoveCustomEditorPath(pathToRemove);
                    }
                };

                flyout.Items.Add(openFolderItem);
                flyout.Items.Add(removeItem);

                moreButton.Flyout = flyout;

                var headerTextBlock = new TextBlock
                {
                    Text = path,
                    IsTextSelectionEnabled = true
                };

                var pathCard = new CommunityToolkit.WinUI.Controls.SettingsCard
                {
                    Header = headerTextBlock,
                    Content = moreButton
                };

                cards.Add(pathCard);
            }
        }

        CustomEditorPathsExpander.ItemsSource = cards;
    }

    private void RemoveCustomEditorPath(string pathToRemove)
    {
        var updatedPaths = _settings.CustomEditorPaths
            .Where(p => !string.Equals(p, pathToRemove, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _settings.CustomEditorPaths = updatedPaths;

        SaveSettings();
        PopulateCustomEditorPathsUI();
    }

    private async void OnAddCustomTemplatePathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;

            var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Add a template search path",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var folderPath = folder.Path;
            var updatedPaths = new List<string>(_settings.CustomTemplatePaths ?? []);
            if (!updatedPaths.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
            {
                updatedPaths.Add(folderPath);
                _settings.CustomTemplatePaths = updatedPaths;
                SaveSettings();
                PopulateCustomTemplatePathsUI();
                CustomTemplatePathsExpander.IsExpanded = true;
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to add template search path: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void PopulateCustomTemplatePathsUI()
    {
        if (CustomTemplatePathsExpander is null)
        {
            return;
        }

        var cards = new List<CommunityToolkit.WinUI.Controls.SettingsCard>();
        if (_settings.CustomTemplatePaths == null || _settings.CustomTemplatePaths.Count == 0)
        {
            cards.Add(new CommunityToolkit.WinUI.Controls.SettingsCard
            {
                Header = "No custom search paths added",
                Description = "Click 'Add folder' to scan another location for project templates.",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var path in _settings.CustomTemplatePaths)
            {
                var moreButton = new Button
                {
                    Content = new FontIcon { Glyph = "\uE712", FontSize = 12 },
                    Style = (Style)Application.Current.Resources["SubtleButtonStyle"]
                };
                ToolTipService.SetToolTip(moreButton, "More options");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    moreButton,
                    $"More options for template search path {path}");

                var flyout = new MenuFlyout();
                var openFolderItem = new MenuFlyoutItem
                {
                    Text = "Open in File Explorer",
                    Icon = new FontIcon { Glyph = "\uE8B7" },
                    Tag = path
                };
                openFolderItem.Click += (s, args) =>
                {
                    if (s is MenuFlyoutItem { Tag: string folderPath })
                    {
                        OpenFolderPath(folderPath);
                    }
                };

                var removeItem = new MenuFlyoutItem
                {
                    Text = "Remove",
                    Icon = new FontIcon { Glyph = "\uE74D" },
                    Tag = path
                };
                removeItem.Click += (s, args) =>
                {
                    if (s is MenuFlyoutItem { Tag: string pathToRemove })
                    {
                        RemoveCustomTemplatePath(pathToRemove);
                    }
                };

                flyout.Items.Add(openFolderItem);
                flyout.Items.Add(removeItem);
                moreButton.Flyout = flyout;

                cards.Add(new CommunityToolkit.WinUI.Controls.SettingsCard
                {
                    Header = new TextBlock
                    {
                        Text = path,
                        IsTextSelectionEnabled = true,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    Content = moreButton
                });
            }
        }

        CustomTemplatePathsExpander.ItemsSource = cards;
    }

    private void RemoveCustomTemplatePath(string pathToRemove)
    {
        _settings.CustomTemplatePaths = (_settings.CustomTemplatePaths ?? [])
            .Where(path => !string.Equals(path, pathToRemove, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveSettings();
        PopulateCustomTemplatePathsUI();
    }

    private static void OpenFolderPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore if folder opening fails
        }
    }

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
            Debug.WriteLine($"[LocationsPage] ShowStatus failed: {ex}");
        }
    }

    private void OnStatusInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (StatusInfoBar is not null)
        {
            StatusInfoBar.Visibility = Visibility.Collapsed;
        }
    }
}
