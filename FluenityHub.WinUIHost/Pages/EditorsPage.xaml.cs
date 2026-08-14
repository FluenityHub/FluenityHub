using System.Collections.ObjectModel;
using System.Diagnostics;
using FluenityHub_WinUIHost.Dialogs;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Pages;

public sealed class EditorListItem
{
    public required UnityEditorInfo Editor { get; init; }
    public required bool CanUseEditor { get; init; }
    public required bool IsUninstalling { get; init; }
    public required int ProjectCount { get; init; }

    public List<TargetPlatformInfo> TargetPlatforms => Editor.InstalledTargetPlatforms ?? [];
    public string ProjectCountLabel => ProjectCount == 1 ? "1 project" : $"{ProjectCount} projects";
    public string ProjectCountTooltip => $"{ProjectCount} project{(ProjectCount == 1 ? "" : "s")} using this Unity Editor version";

    public string LaunchButtonText => IsUninstalling ? "Uninstalling" : "Launch";
    public Visibility LaunchIconVisibility =>
        IsUninstalling ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UninstallProgressVisibility =>
        IsUninstalling ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class EditorsPage : Page
{
    private readonly UnityEditorLocator _editorLocator = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly UnityHubLocationSettingsService _unityHubLocationSettingsService = new();
    private readonly UnityHubProjectService _projectService = new();
    private readonly UnityEditorInstallationService _editorInstallationService = new();
    private readonly UnityEditorLaunchService _editorLaunchService = new();
    private readonly UnityModuleInstallationManager _moduleInstallationManager =
        UnityModuleInstallationManager.Instance;
    private readonly List<UnityEditorInfo> _allEditors = [];
    private readonly HashSet<string> _uninstallingEditorVersions =
        new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<UnityModuleInstallationListItem> ModuleOperations { get; } = [];
    private AppSettings _settings = new();
    private bool _hasLoadedEditors;
    private bool _isReloadingEditors;

    public EditorsPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _moduleInstallationManager.OperationChanged += OnModuleInstallationChanged;
        SyncModuleOperations();
        if (!_hasLoadedEditors)
        {
            _hasLoadedEditors = true;
            await ReloadEditorsAsync();
        }
        else
        {
            ApplyFilterAndSort();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _moduleInstallationManager.OperationChanged -= OnModuleInstallationChanged;
    }

    private async Task ReloadEditorsAsync()
    {
        if (_isReloadingEditors)
        {
            return;
        }

        _isReloadingEditors = true;
        try
        {
            SetEditorsLoadingState(true);
            await Task.Yield();

            var loadResult = await Task.Run(() =>
            {
                var settings = _settingsStore.Load();
                var editors = _editorLocator
                    .GetInstalledEditorDetails(settings.CustomEditorPaths)
                    .ToList();
                return (Settings: settings, Editors: editors);
            });

            _settings = loadResult.Settings;
            _allEditors.Clear();
            _allEditors.AddRange(loadResult.Editors);

            ApplyFilterAndSort();
        }
        catch (Exception ex)
        {
            ShowStatus(
                "Installed editors could not be loaded",
                ex.Message,
                InfoBarSeverity.Error);
        }
        finally
        {
            SetEditorsLoadingState(false);
            _isReloadingEditors = false;
        }
    }

    private void SetEditorsLoadingState(bool isLoading)
    {
        if (LoadingPanel is not null)
        {
            LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        }
        if (LoadingRing is not null)
        {
            LoadingRing.IsActive = isLoading;
        }
        if (EditorsListView is not null)
        {
            EditorsListView.Opacity = isLoading ? 0.5 : 1.0;
            EditorsListView.Visibility = Visibility.Visible;
        }
        if (EmptyStatePanel is not null && isLoading)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyFilterAndSort()
    {
        if (EditorsListView is null || SearchBox is null || SummaryTextBlock is null || EmptyStatePanel is null)
        {
            return;
        }

        var query = SearchBox.Text?.Trim() ?? string.Empty;

        IEnumerable<UnityEditorInfo> editors = _allEditors;
        if (!string.IsNullOrWhiteSpace(query))
        {
            editors = editors.Where(editor =>
                editor.Version.Contains(query, StringComparison.OrdinalIgnoreCase)
                || editor.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || editor.InstallDirectory.Contains(query, StringComparison.OrdinalIgnoreCase)
                || editor.ExecutablePath.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var sortIndex = GetSortIndex();
        editors = sortIndex switch
        {
            1 => editors.OrderBy(editor => editor.Version, StringComparer.OrdinalIgnoreCase),
            2 => editors.OrderBy(editor => editor.InstallDirectory, StringComparer.OrdinalIgnoreCase),
            _ => editors.OrderByDescending(editor => editor.Version, StringComparer.OrdinalIgnoreCase)
        };

        var filteredEditors = editors.ToList();
        var busyEditorVersions = _moduleInstallationManager.ActiveOperations
            .Select(operation => operation.EditorVersion)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allProjects = _projectService.GetRecentProjects();
        var filteredList = filteredEditors
            .Select(editor => new EditorListItem
            {
                Editor = editor,
                CanUseEditor = !busyEditorVersions.Contains(editor.Version)
                    && !_uninstallingEditorVersions.Contains(editor.Version),
                IsUninstalling = _uninstallingEditorVersions.Contains(editor.Version),
                ProjectCount = allProjects.Count(p => string.Equals(p.Version, editor.Version, StringComparison.OrdinalIgnoreCase))
            })
            .ToList();
        EditorsListView.ItemsSource = filteredList;

        var count = filteredEditors.Count;
        SummaryTextBlock.Text = $"{count} installed Unity Editor{(count == 1 ? "" : "s")}";

        EmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorsListView.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private int GetSortIndex()
    {
        if (SortOldestItem?.IsChecked == true) return 1;
        if (SortDirectoryItem?.IsChecked == true) return 2;
        return 0;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
        {
            return;
        }

        ApplyFilterAndSort();
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ApplyFilterAndSort();
    }

    private void OnSortOptionClick(object sender, RoutedEventArgs e)
    {
        ApplyFilterAndSort();
    }

    private async void OnLocateEditorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            try
            {
                button.IsEnabled = false;

                var windowId = button.XamlRoot.ContentIslandEnvironment.AppWindowId;
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
                {
                    Title = "Add a Unity Editor",
                    CommitButtonText = "Select folder",
                    SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                    ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
                };

                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    var folderPath = folder.Path;
                    _settings = _settingsStore.Load();
                    var updatedPaths = new List<string>(_settings.CustomEditorPaths);
                    if (!updatedPaths.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
                    {
                        updatedPaths.Add(folderPath);
                        _settings.CustomEditorPaths = updatedPaths;
                        _settingsStore.Save(_settings);
                    }

                    await ReloadEditorsAsync();
                    ShowStatus($"Located Unity Editor folder: {folderPath}", InfoBarSeverity.Success);
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to locate Editor folder: {ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await ReloadEditorsAsync();
    }


    private void OnItemRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        // MenuFlyout opens automatically via ContextFlyout property
    }

    private async void OnContextLaunchEditorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UnityEditorInfo editor })
        {
            if (_moduleInstallationManager.IsEditorBusy(editor.Version))
            {
                ShowStatus(
                    $"Unity {editor.Version} is being updated. You can launch another editor version while it finishes.",
                    InfoBarSeverity.Warning);
                return;
            }

            await LaunchEditorAsync(editor);
        }
    }

    private void OnContextOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UnityEditorInfo editor })
        {
            OpenFolder(editor.InstallDirectory);
        }
    }

    private async void OnResetSandboxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: UnityEditorInfo editor })
        {
            return;
        }

        try
        {
            var targetTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default;

            var dialog = new ContentDialog
            {
                Title = $"Reset Sandbox for Unity {editor.Version}?",
                Content = "This will clear all test assets and scenes from your Sandbox workspace. This action cannot be undone.",
                PrimaryButtonText = "Reset Sandbox",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = targetTheme,
                Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
            };

            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary)
            {
                var success = _editorLaunchService.ResetSandbox(editor.Version);
                if (success)
                {
                    ShowStatus($"Sandbox workspace reset for Unity {editor.Version}.", InfoBarSeverity.Success);
                }
                else
                {
                    ShowStatus($"Unable to reset Sandbox workspace for Unity {editor.Version}.", InfoBarSeverity.Error);
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to reset Sandbox: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnAddModulesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: UnityEditorInfo editor })
        {
            return;
        }

        if (_moduleInstallationManager.IsEditorBusy(editor.Version))
        {
            ShowStatus(
                $"Modules are already queued or installing for Unity {editor.Version}.",
                InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var projectCount = _projectService
                .GetRecentProjects()
                .Count(project => project.Version.Equals(editor.Version, StringComparison.OrdinalIgnoreCase));
            var targetTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default;

            var dialog = new AddModulesDialog(editor, projectCount)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = targetTheme
            };

            await dialog.ShowAsync();
            if (dialog.InstallationRequest is not null)
            {
                var request = dialog.InstallationRequest;
                if (request.OperationKind == UnityModuleOperationKind.Remove
                    && !await ConfirmModuleRemovalAsync(request))
                {
                    return;
                }

                var result = _moduleInstallationManager.Enqueue(request);
                ShowStatus(
                    result.Message,
                    result.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                if (result.Accepted)
                {
                    SyncModuleOperations();
                    ApplyFilterAndSort();
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to open Add modules: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ConfirmModuleRemovalAsync(UnityModuleInstallationRequest request)
    {
        var moduleName = request.Modules[0].Name;
        var dialog = new ContentDialog
        {
            Title = "Remove module?",
            Content = $"Remove {moduleName} from Unity {request.EditorVersion}? This deletes the module's files from this Editor.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnInstallEditorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default;

            var dialog = new InstallEditorDialog(_allEditors.Select(editor => editor.Version))
            {
                XamlRoot = XamlRoot,
                RequestedTheme = targetTheme
            };
            await dialog.ShowAsync();
            if (dialog.SelectedRelease is null)
            {
                return;
            }

            _settings = _settingsStore.Load();
            var installRoot = _unityHubLocationSettingsService.GetInstallLocation();
            var modulesDialog = new AddModulesDialog(dialog.SelectedRelease, installRoot)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = targetTheme
            };
            await modulesDialog.ShowAsync();
            if (modulesDialog.InstallationRequest is null)
            {
                return;
            }

            var result = _moduleInstallationManager.Enqueue(modulesDialog.InstallationRequest);
            ShowStatus(
                result.Message,
                result.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (result.Accepted)
            {
                SyncModuleOperations();
            }
        }
        catch (Exception ex)
        {
            ShowStatus(
                "Unable to open the Editor installer",
                ex.Message,
                InfoBarSeverity.Error);
        }
    }

    private void OnContextReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UnityEditorInfo editor })
        {
            OpenReleaseNotes(editor.Version);
        }
    }

    private async void OnVerifyIntegrityClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: UnityEditorInfo editor })
            {
                var dialog = new Dialogs.EditorIntegrityDialog(editor)
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                        ?? MainWindow.Instance?.CurrentTheme
                        ?? ElementTheme.Default
                };
                await dialog.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Unable to open Installation Repair dialog", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnProjectCountBadgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string version } && !string.IsNullOrWhiteSpace(version))
        {
            MainPage.Instance?.NavigateToProjectsFilteredByEditor(version);
        }
    }

    private void OnViewAssociatedProjectsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string version } && !string.IsNullOrWhiteSpace(version))
        {
            MainPage.Instance?.NavigateToProjectsFilteredByEditor(version);
        }
    }

    private async void OnUninstallEditorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: UnityEditorInfo editor })
        {
            return;
        }

        if (_moduleInstallationManager.IsEditorBusy(editor.Version)
            || _uninstallingEditorVersions.Contains(editor.Version))
        {
            ShowStatus(
                $"{editor.DisplayName} is currently being updated and cannot be uninstalled.",
                InfoBarSeverity.Warning);
            return;
        }

        if (IsEditorRunning(editor.ExecutablePath))
        {
            ShowStatus(
                $"Close {editor.DisplayName} before uninstalling it.",
                InfoBarSeverity.Warning);
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
            Title = $"Uninstall {editor.DisplayName}?",
            Content = new TextBlock
            {
                Text = $"This removes the Editor and its installed modules from:\n{editor.InstallDirectory}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _uninstallingEditorVersions.Add(editor.Version);
        ApplyFilterAndSort();
        MainWindow.Instance?.NotifyEditorUninstallStarted();
        ShowStatus(
            "Uninstalling Unity Editor",
            $"Removing {editor.DisplayName}…",
            InfoBarSeverity.Success);
        var succeeded = false;
        var completionMessage = $"Unable to uninstall {editor.DisplayName}.";
        try
        {
            var result = await _editorInstallationService.UninstallAsync(
                editor.Version,
                outputObserver: null,
                CancellationToken.None);
            succeeded = result.Succeeded;
            completionMessage = result.Message;
            ShowStatus(
                result.Succeeded ? "Unity Editor uninstalled" : "Uninstall failed",
                result.Message,
                result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            completionMessage = ex.Message;
            ShowStatus("Uninstall failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            MainWindow.Instance?.NotifyEditorUninstallCompleted(
                editor.DisplayName,
                succeeded,
                completionMessage);
            _uninstallingEditorVersions.Remove(editor.Version);
            await ReloadEditorsAsync();
        }
    }

    private static bool IsEditorRunning(string executablePath)
    {
        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName("Unity"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(
                            Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                            targetPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Some elevated processes do not expose MainModule.
                }
            }
        }

        return false;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string folderPath })
        {
            OpenFolder(folderPath);
        }
    }

    private void OnViewReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string version })
        {
            OpenReleaseNotes(version);
        }
    }

    private async void OpenReleaseNotes(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        try
        {
            var url = $"https://unity.com/releases/editor/whats-new/{version}#notes";
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OpenReleaseNotes failed: {ex}");
        }
    }

    private async void OnLaunchEditorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: UnityEditorInfo editor })
        {
            if (_moduleInstallationManager.IsEditorPathBusy(editor.ExecutablePath))
            {
                ShowStatus(
                    "This Unity Editor is being updated. You can launch another editor version while it finishes.",
                    InfoBarSeverity.Warning);
                return;
            }

            await LaunchEditorAsync(editor);
        }
    }

    private void OnCancelModuleOperationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid operationId }
            || operationId == Guid.Empty)
        {
            return;
        }

        if (!_moduleInstallationManager.Cancel(operationId))
        {
            ShowStatus("The installation could not be canceled because it has already finished.", InfoBarSeverity.Warning);
        }
    }

    private void OnPauseModuleOperationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid operationId }
            || operationId == Guid.Empty)
        {
            return;
        }

        if (!_moduleInstallationManager.Pause(operationId))
        {
            ShowStatus("Unable to pause installation", "The installation is no longer active.", InfoBarSeverity.Warning);
        }
    }

    private void OnResumeModuleOperationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid operationId }
            || operationId == Guid.Empty)
        {
            return;
        }

        var result = _moduleInstallationManager.Resume(operationId);
        if (!result.Accepted)
        {
            ShowStatus("Unable to resume installation", result.Message, InfoBarSeverity.Error);
        }
    }

    private void OnOpenModuleLogClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string logFilePath }
            || string.IsNullOrWhiteSpace(logFilePath))
        {
            return;
        }

        if (!File.Exists(logFilePath))
        {
            ShowStatus("The installation log could not be found.", InfoBarSeverity.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = logFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to open the installation log: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnOpenModuleLogFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string logFilePath }
            || string.IsNullOrWhiteSpace(logFilePath))
        {
            return;
        }

        var folderPath = Path.GetDirectoryName(logFilePath);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            ShowStatus("The log directory could not be found.", InfoBarSeverity.Error);
            return;
        }

        OpenFolder(folderPath);
    }

    private void OnRetryModuleOperationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid operationId }
            || operationId == Guid.Empty)
        {
            return;
        }

        var result = _moduleInstallationManager.Retry(operationId);
        if (!result.Accepted)
        {
            ShowStatus("Unable to retry installation", result.Message, InfoBarSeverity.Error);
        }
    }

    private void OnClearInactiveModuleOperationsClick(object sender, RoutedEventArgs e)
    {
        var clearedCount = _moduleInstallationManager.DismissInactive();
        if (clearedCount == 0)
        {
            return;
        }

        ShowStatus(
            "Inactive installations cleared",
            $"Removed {clearedCount} canceled or failed installation{(clearedCount == 1 ? string.Empty : "s")} from this list. Downloaded files were not removed.",
            InfoBarSeverity.Success);
    }

    private void OnModuleInstallationChanged(
        object? sender,
        UnityModuleInstallationChangedEventArgs args)
    {
        var changed = args.ChangedOperation;
        if (changed?.IsTerminal == true)
        {
            var editorsAlreadyReloaded = false;
            switch (changed.State)
            {
                case UnityModuleInstallationState.Succeeded:
                    _ = ReloadEditorsAsync();
                    editorsAlreadyReloaded = true;
                    ShowStatus(
                        changed.OperationKind switch
                        {
                            UnityModuleOperationKind.Repair => "Repair complete",
                            UnityModuleOperationKind.Remove => "Removal complete",
                            _ => "Installation complete"
                        },
                        changed.Message,
                        InfoBarSeverity.Success);
                    break;
                case UnityModuleInstallationState.Failed:
                    ShowStatus(
                        changed.OperationKind switch
                        {
                            UnityModuleOperationKind.Repair => "Repair failed",
                            UnityModuleOperationKind.Remove => "Removal failed",
                            _ => "Installation failed"
                        },
                        changed.Message,
                        InfoBarSeverity.Error);
                    break;
                case UnityModuleInstallationState.Canceled:
                    ShowStatus(
                        changed.OperationKind switch
                        {
                            UnityModuleOperationKind.Repair => "Repair canceled",
                            UnityModuleOperationKind.Remove => "Removal canceled",
                            _ => "Installation canceled"
                        },
                        changed.Message,
                        InfoBarSeverity.Warning);
                    break;
            }

            if (!editorsAlreadyReloaded)
            {
                ApplyFilterAndSort();
            }
        }

        SyncModuleOperations();
    }

    private void SyncModuleOperations()
    {
        if (DownloadsExpander is null)
        {
            return;
        }

        var snapshots = _moduleInstallationManager.VisibleOperations;
        ClearInactiveInstallsMenuItem.IsEnabled = snapshots.Any(snapshot =>
            snapshot.State is UnityModuleInstallationState.Canceled or UnityModuleInstallationState.Failed);
        var visibleIds = snapshots.Select(snapshot => snapshot.Id).ToHashSet();

        for (var index = ModuleOperations.Count - 1; index >= 0; index--)
        {
            if (!visibleIds.Contains(ModuleOperations[index].Id))
            {
                ModuleOperations.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < snapshots.Count; targetIndex++)
        {
            var snapshot = snapshots[targetIndex];
            var item = ModuleOperations.FirstOrDefault(operation => operation.Id == snapshot.Id);
            if (item is null)
            {
                var iconPath = _allEditors.FirstOrDefault(
                    editor => string.Equals(
                        editor.Version,
                        snapshot.EditorVersion,
                        StringComparison.OrdinalIgnoreCase))?.IconPath
                    ?? _allEditors
                        .Select(editor => editor.IconPath)
                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                item = new UnityModuleInstallationListItem(snapshot, iconPath);
                ModuleOperations.Insert(Math.Min(targetIndex, ModuleOperations.Count), item);
            }
            else
            {
                item.Update(snapshot);
                var currentIndex = ModuleOperations.IndexOf(item);
                if (currentIndex != targetIndex)
                {
                    ModuleOperations.Move(currentIndex, targetIndex);
                }
            }
        }

        var operationCount = ModuleOperations.Count;
        var expanderVisibility = operationCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (DownloadsExpander.Visibility != expanderVisibility)
        {
            DownloadsExpander.Visibility = expanderVisibility;
        }

        var activeOperations = _moduleInstallationManager.ActiveOperations;
        var activeCount = activeOperations.Count;
        var description = activeCount switch
        {
            0 when snapshots.FirstOrDefault()?.State == UnityModuleInstallationState.Paused
                => "An installation is paused",
            0 when snapshots.FirstOrDefault()?.State == UnityModuleInstallationState.Failed
                => "The last installation failed. Expand for details and the full log.",
            0 when snapshots.Count > 0
                => "Recent module installations",
            1 => "1 installation is active or queued",
            _ => $"{activeCount} installations are active or queued"
        };
        if (!string.Equals(DownloadsExpander.Description as string, description, StringComparison.Ordinal))
        {
            DownloadsExpander.Description = description;
        }

        UpdateDownloadsSummary(activeOperations, snapshots);
    }

    private void UpdateDownloadsSummary(
        IReadOnlyList<UnityModuleInstallationSnapshot> activeOperations,
        IReadOnlyList<UnityModuleInstallationSnapshot> visibleOperations)
    {
        if (DownloadsSummaryProgressBar is null
            || DownloadsSummaryTextBlock is null
            || DownloadsSummaryPercentageTextBlock is null)
        {
            return;
        }

        if (activeOperations.Count > 0)
        {
            var current = activeOperations.First();
            var weightedTotal = activeOperations.Sum(operation =>
                Math.Max(operation.DownloadSizeBytes, 1));
            var percentage = weightedTotal <= 0
                ? current.Percentage
                : activeOperations.Sum(operation =>
                    (operation.Percentage ?? 0) * Math.Max(operation.DownloadSizeBytes, 1))
                  / weightedTotal;
            var hasKnownProgress = activeOperations.Any(operation => operation.Percentage is not null);

            DownloadsSummaryTextBlock.Text = activeOperations.Count == 1
                ? current.Phase
                : $"{activeOperations.Count} installations · {current.Phase}";
            DownloadsSummaryProgressBar.IsIndeterminate =
                !hasKnownProgress
                && current.State is not UnityModuleInstallationState.Queued;
            DownloadsSummaryProgressBar.ShowError = false;
            DownloadsSummaryProgressBar.ShowPaused =
                current.State is UnityModuleInstallationState.Canceling
                    or UnityModuleInstallationState.Pausing
                    or UnityModuleInstallationState.Paused;
            DownloadsSummaryProgressBar.Value = percentage ?? 0;
            DownloadsSummaryProgressBar.Visibility = Visibility.Visible;
            DownloadsSummaryPercentageTextBlock.Text =
                hasKnownProgress ? $"{percentage ?? 0:0}%" : string.Empty;
            DownloadsSummaryPercentageTextBlock.Visibility =
                hasKnownProgress ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var latest = visibleOperations.FirstOrDefault();
        if (latest is null)
        {
            DownloadsSummaryTextBlock.Text = "No active installations";
            DownloadsSummaryProgressBar.Visibility = Visibility.Collapsed;
            DownloadsSummaryPercentageTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        DownloadsSummaryTextBlock.Text = latest.Phase;
        DownloadsSummaryProgressBar.IsIndeterminate = false;
        DownloadsSummaryProgressBar.ShowError =
            latest.State == UnityModuleInstallationState.Failed;
        DownloadsSummaryProgressBar.ShowPaused =
            latest.State is UnityModuleInstallationState.Canceled
                or UnityModuleInstallationState.Pausing
                or UnityModuleInstallationState.Paused;
        DownloadsSummaryProgressBar.Value =
            latest.State == UnityModuleInstallationState.Succeeded ? 100 : latest.Percentage ?? 0;
        DownloadsSummaryProgressBar.Visibility = Visibility.Visible;
        DownloadsSummaryPercentageTextBlock.Text =
            latest.State == UnityModuleInstallationState.Succeeded ? "100%" : string.Empty;
        DownloadsSummaryPercentageTextBlock.Visibility =
            latest.State == UnityModuleInstallationState.Succeeded
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OpenFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            ShowStatus($"Directory not found: {folderPath}", InfoBarSeverity.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
            ShowStatus("Editor folder opened in File Explorer.", InfoBarSeverity.Success);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = folderPath,
                    UseShellExecute = true
                });
                ShowStatus("Editor folder opened in File Explorer.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"Unable to open folder: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private async Task LaunchEditorAsync(UnityEditorInfo editor)
    {
        try
        {
            var targetTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default;

            var matchingRecentProject = _projectService.GetRecentProjects()
                .Where(p => string.Equals(p.Version, editor.Version, StringComparison.OrdinalIgnoreCase)
                         || p.Version.StartsWith(editor.Version, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.LastModifiedUtc)
                .FirstOrDefault(p => File.Exists(System.IO.Path.Combine(p.Path, "ProjectSettings", "ProjectVersion.txt")));

            var dialog = new LaunchEditorDialog(editor, matchingRecentProject)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = targetTheme
            };

            await dialog.ShowAsync();

            switch (dialog.SelectedChoice)
            {
                case LaunchEditorChoice.RecentProject when dialog.TargetRecentProject is not null:
                    {
                        var result = await _editorLaunchService.LaunchProjectAsync(
                            editor.ExecutablePath,
                            dialog.TargetRecentProject.Path,
                            editor.Version);
                        if (!result.Succeeded)
                        {
                            ShowStatus(result.Message, InfoBarSeverity.Error);
                            return;
                        }
                        ShowStatus($"Launching {editor.DisplayName} with '{dialog.TargetRecentProject.Title}'...", InfoBarSeverity.Success);
                        MainWindow.Instance?.NotifyEditorLaunched(result.EditorProcess);
                        break;
                    }

                case LaunchEditorChoice.Sandbox:
                    {
                        var result = await _editorLaunchService.LaunchBlankEditorAsync(editor);
                        if (!result.Succeeded)
                        {
                            ShowStatus(result.Message, InfoBarSeverity.Error);
                            return;
                        }

                        ShowStatus($"Launching {editor.DisplayName} (Sandbox Mode)...", InfoBarSeverity.Success);
                        MainWindow.Instance?.NotifyEditorLaunched(result.EditorProcess);
                        break;
                    }

                case LaunchEditorChoice.NewProject:
                    {
                        var installedVersions = _allEditors.Select(e => e.Version).ToList();
                        var newProjDialog = new NewProjectDialog(installedVersions, selectedTemplate: null, initialVersion: editor.Version)
                        {
                            XamlRoot = XamlRoot,
                            RequestedTheme = targetTheme
                        };

                        await newProjDialog.ShowAsync();
                        if (!string.IsNullOrEmpty(newProjDialog.CreatedProjectPath))
                        {
                            var result = await _editorLaunchService.LaunchProjectAsync(
                                editor.ExecutablePath,
                                newProjDialog.CreatedProjectPath,
                                editor.Version);
                            if (!result.Succeeded)
                            {
                                ShowStatus(result.Message, InfoBarSeverity.Error);
                                return;
                            }

                            ShowStatus($"Launching {editor.DisplayName} with project '{newProjDialog.CreatedProjectTitle}'...", InfoBarSeverity.Success);
                            MainWindow.Instance?.NotifyEditorLaunched(result.EditorProcess);
                        }
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to launch editor: {ex.Message}", InfoBarSeverity.Error);
        }
    }


    private CancellationTokenSource? _infoBarCts;
    private CancellationTokenSource? _downloadsInfoBarCts;

    private void ShowStatus(string message, InfoBarSeverity severity)
        => ShowStatus(
            severity switch
            {
                InfoBarSeverity.Error => "Something went wrong",
                InfoBarSeverity.Warning => "Action needed",
                InfoBarSeverity.Success => "Completed",
                _ => "Status"
            },
            message,
            severity);

    private async void ShowStatus(
        string title,
        string message,
        InfoBarSeverity severity)
    {
        try
        {
            if (message.Contains("Queued", StringComparison.OrdinalIgnoreCase)
                || message.Contains("installation", StringComparison.OrdinalIgnoreCase)
                || message.Contains("module", StringComparison.OrdinalIgnoreCase))
            {
                ShowDownloadsStatus(title, message, severity);
                return;
            }

            if (StatusInfoBar is null)
            {
                return;
            }

            _infoBarCts?.Cancel();
            _infoBarCts = new CancellationTokenSource();
            var token = _infoBarCts.Token;

            StatusInfoBar.Title = title;
            StatusInfoBar.Message = message;
            StatusInfoBar.Severity = severity;
            StatusInfoBar.IsOpen = true;

            if (severity is InfoBarSeverity.Error or InfoBarSeverity.Warning)
            {
                return;
            }

            try
            {
                await Task.Delay(6000, token);
                if (!token.IsCancellationRequested)
                {
                    StatusInfoBar.IsOpen = false;
                }
            }
            catch (TaskCanceledException)
            {
                // Reset by newer message
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowStatus error: {ex}");
        }
    }

    private async void ShowDownloadsStatus(
        string title,
        string message,
        InfoBarSeverity severity)
    {
        try
        {
            if (DownloadsInfoBar is null)
            {
                if (StatusInfoBar is not null)
                {
                    StatusInfoBar.Title = title;
                    StatusInfoBar.Message = message;
                    StatusInfoBar.Severity = severity;
                    StatusInfoBar.IsOpen = true;
                }
                return;
            }

            _downloadsInfoBarCts?.Cancel();
            _downloadsInfoBarCts = new CancellationTokenSource();
            var token = _downloadsInfoBarCts.Token;

            DownloadsInfoBar.Title = title;
            DownloadsInfoBar.Message = message;
            DownloadsInfoBar.Severity = severity;
            DownloadsInfoBar.IsOpen = true;

            if (severity is InfoBarSeverity.Error or InfoBarSeverity.Warning)
            {
                return;
            }

            try
            {
                await Task.Delay(6000, token);
                if (!token.IsCancellationRequested)
                {
                    DownloadsInfoBar.IsOpen = false;
                }
            }
            catch (TaskCanceledException)
            {
                // Reset by newer message
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowDownloadsStatus error: {ex}");
        }
    }
}
