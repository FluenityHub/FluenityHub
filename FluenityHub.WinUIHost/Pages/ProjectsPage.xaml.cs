using System.Diagnostics;
using System.Globalization;
using FluenityHub_WinUIHost.Dialogs;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluenityHub_WinUIHost.Pages;

public sealed partial class ProjectsPage : Page
{
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility HasGlyphToVisibility(string glyph) => !string.IsNullOrEmpty(glyph) ? Visibility.Visible : Visibility.Collapsed;

    private readonly UnityHubProjectService _projectService = new();
    private readonly UnityEditorLocator _editorLocator = new();
    private readonly IdeDetector _ideDetector = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly ProjectBackupService _projectBackupService = new();
    private readonly UnityEditorReleaseService _editorReleaseService = new();
    private readonly UnityEditorLaunchService _editorLaunchService = new();
    private readonly UnityProjectShareLinkService _projectShareLinkService = new();
    private readonly WindowsShareService _windowsShareService = new();
    private readonly UnityHubLocationSettingsService _unityHubLocationSettingsService = new();
    private readonly UnityModuleInstallationManager _moduleInstallationManager =
        UnityModuleInstallationManager.Instance;
    private readonly List<UnityProjectInfo> _allProjects = [];
    private readonly Dictionary<string, string> _installedEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<TargetPlatformInfo>> _installedPlatforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProjectListItemViewModel> _projectRows = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = new();
    private object? _hoveredHeaderButton;
    private string _sortCriteria = "LastModified"; // "Name", "LastModified", "EditorVersion", "Platform"
    private bool _sortAscending = false;
    private bool _isSyncingFlyout;
    private bool _isReloadingData;
    private bool _hasCompletedEditorDiscovery;
    private DateTime _projectStoreChangeStampUtc;
    private sealed record ProjectSourceControlRefresh(
        string ProjectPath,
        SourceControlDetectionResult? SourceControl);
    private bool _keepStarredOnTop = true;
    private bool _keepSourceControlOnTop;
    private readonly HashSet<string> _selectedTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedEditorFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedPlatformFilters = new(StringComparer.OrdinalIgnoreCase);
    private string _groupByMode = "None"; // "None", "Folder", "SourceControl", "EditorVersion"
    private readonly HashSet<string> _collapsedProjectGroupKeys = new(StringComparer.Ordinal);

    [Conditional("DEBUG")]
    private static void SortLog(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Debug.WriteLine(line);
    }

    public ProjectsPage()
    {
        InitializeComponent();
        ReloadData(showSuccessMessage: false);
    }

    private async void ReloadData(bool showSuccessMessage)
    {
        if (_isReloadingData)
        {
            return;
        }

        _isReloadingData = true;
        try
        {
            SetProjectsLoadingState(true);
            await Task.Yield();

            var initialResult = await Task.Run(() =>
            {
                var settings = _settingsStore.Load();
                // The first frame only parses Unity Hub's project index. File
                // repair and per-project ProductName reads happen in the
                // authoritative background refresh below.
                var projects = _projectService.GetRecentProjects(
                    repairProjectsFile: false,
                    resolveProductNames: false).ToList();

                // Older FluenityHub builds stored assignments only in app
                // settings. Migrate those entries once into Unity Hub's own
                // per-project tags array, then let the shared Hub data remain
                // authoritative. Tag colors intentionally stay app-local.
                settings.ProjectTags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var settingsChanged = false;
                foreach (var project in projects)
                {
                    if (!settings.ProjectTags.TryGetValue(project.Path, out var legacyTags))
                    {
                        continue;
                    }

                    if (project.Tags.Count > 0)
                    {
                        settingsChanged |= settings.ProjectTags.Remove(project.Path);
                        continue;
                    }

                    var normalizedLegacyTags = UnityHubProjectService.NormalizeProjectTags(legacyTags);
                    if (_projectService.UpdateProjectTags(project.Path, normalizedLegacyTags))
                    {
                        project.Tags.Clear();
                        project.Tags.AddRange(normalizedLegacyTags);
                        settingsChanged |= settings.ProjectTags.Remove(project.Path);
                    }
                }

                if (settingsChanged)
                {
                    _settingsStore.Save(settings);
                }

                return (Settings: settings, Projects: projects);
            });

            _settings = initialResult.Settings;
            if (ShowFavoritesColumnItem is not null) ShowFavoritesColumnItem.IsChecked = _settings.ShowFavoritesColumn;
            if (ShowSourceControlColumnItem is not null) ShowSourceControlColumnItem.IsChecked = _settings.ShowSourceControlColumn;
            if (ShowModifiedColumnItem is not null) ShowModifiedColumnItem.IsChecked = _settings.ShowModifiedColumn;
            if (ShowEditorVersionColumnItem is not null) ShowEditorVersionColumnItem.IsChecked = _settings.ShowEditorVersionColumn;
            if (ShowPlatformColumnItem is not null) ShowPlatformColumnItem.IsChecked = _settings.ShowPlatformColumn;
            if (HideMissingProjectsItem is not null) HideMissingProjectsItem.IsChecked = _settings.HideMissingProjects;

            _sortCriteria = !string.IsNullOrEmpty(_settings.SortCriteria) ? _settings.SortCriteria : "LastModified";
            _sortAscending = _settings.SortAscending;
            _keepStarredOnTop = _settings.KeepStarredOnTop;
            _keepSourceControlOnTop = _settings.KeepSourceControlOnTop;
            _selectedEditorFilters.Clear();
            _selectedEditorFilters.UnionWith(_settings.ProjectEditorFilters ?? []);
            _selectedPlatformFilters.Clear();
            _selectedPlatformFilters.UnionWith(_settings.ProjectPlatformFilters ?? []);
            _selectedTagFilters.Clear();
            _selectedTagFilters.UnionWith(_settings.ProjectTagFilters ?? []);

            _allProjects.Clear();
            foreach (var proj in initialResult.Projects)
            {
                _allProjects.Add(proj);
            }

            RebuildProjectFilterMenus();

            // Make the recent-project list usable as soon as the Hub data is
            // available. Editor discovery and repository probing can touch
            // many folders and must not hold the first interactive frame.
            ApplyFilterAndSort(refreshSourceControl: false);
            SetProjectsLoadingState(false);

            var customEditorPaths = _settings.CustomEditorPaths;
            var editorsTask = Task.Run(() =>
                _editorLocator.GetInstalledEditors(customEditorPaths)
                    .Select(editor => (
                        Version: editor.Key,
                        ExecutablePath: editor.Value,
                        Platforms: (IReadOnlyList<TargetPlatformInfo>)_editorLocator.GetInstalledTargetPlatforms(editor.Value)))
                    .ToList());
            var sourceControlsTask = Task.Run(() =>
                _settings.EnableSourceControl
                    ? initialResult.Projects
                        .Select(project => new ProjectSourceControlRefresh(
                            project.Path,
                            SourceControlDetectionService.Detect(project)))
                        .ToList()
                    : []);
            // Keep Unity Hub's file repair off the first frame. Its result is
            // authoritative for the next reload, but replacing live rows here
            // would make the list jump after it is already interactive.
            var projectsRepairTask = Task.Run(() => _projectService.GetRecentProjects());

            _installedEditors.Clear();
            _installedPlatforms.Clear();
            foreach (var (version, executablePath, platforms) in await editorsTask)
            {
                _installedEditors[version] = executablePath;
                _installedPlatforms[version] = platforms;
            }
            _hasCompletedEditorDiscovery = true;

            var sourceControls = await sourceControlsTask;

            var sourceControlsByPath = sourceControls.ToDictionary(
                item => item.ProjectPath,
                StringComparer.OrdinalIgnoreCase);
            foreach (var project in _allProjects)
            {
                sourceControlsByPath.TryGetValue(project.Path, out var refreshed);
                ApplySourceControlResult(project, refreshed?.SourceControl);
            }

            var editorVersions = _installedEditors.Keys.ToList();
            foreach (var viewModel in _projectRows.Values)
            {
                var isEditorInstalled = !string.IsNullOrWhiteSpace(
                    _editorLocator.FindEditorExecutable(
                        viewModel.Project.Version,
                        _installedEditors));
                viewModel.RefreshRuntimeState(
                    isEditorInstalled,
                    editorVersions,
                    GetInstalledTargetPlatforms(viewModel.SelectedEditorVersion));
            }

            var repairedProjects = await projectsRepairTask;
            ApplyAuthoritativeProjectVersions(repairedProjects);
        }
        catch (InvalidDataException ex)
        {
            ShowStatus($"Data format is invalid: {ex.Message}", InfoBarSeverity.Error);
        }
        catch (IOException ex)
        {
            ShowStatus($"Unable to read data: {ex.Message}", InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowStatus($"Permission denied while reading data: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _projectStoreChangeStampUtc = _projectService.GetProjectStoreChangeStampUtc();
            SetProjectsLoadingState(false);
            _isReloadingData = false;
        }
    }

    public void RefreshExternalProjectMetadata()
    {
        if (_isReloadingData)
        {
            return;
        }

        var currentStamp = _projectService.GetProjectStoreChangeStampUtc();
        if (currentStamp <= _projectStoreChangeStampUtc)
        {
            return;
        }

        ReloadData(showSuccessMessage: false);
    }

    private void SetProjectsLoadingState(bool isLoading)
    {
        if (LoadingPanel is not null)
        {
            LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        }
        if (LoadingRing is not null)
        {
            LoadingRing.IsActive = isLoading;
        }
        if (ProjectsListView is not null)
        {
            ProjectsListView.Opacity = isLoading ? 0.5 : 1.0;
            ProjectsListView.Visibility = Visibility.Visible;
        }
        if (EmptyStatePanel is not null && isLoading)
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyAuthoritativeProjectVersions(IEnumerable<UnityProjectInfo> authoritativeProjects)
    {
        var versionsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in authoritativeProjects)
        {
            versionsByPath[project.Path] = project.Version;
        }
        var changed = false;

        foreach (var project in _allProjects)
        {
            if (!versionsByPath.TryGetValue(project.Path, out var diskVersion)
                || string.IsNullOrWhiteSpace(diskVersion)
                || string.Equals(project.Version, diskVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            project.Version = diskVersion;
            if (_projectRows.TryGetValue(project.Path, out var row))
            {
                row.SelectedEditorVersion = diskVersion;
                row.RefreshRuntimeState(
                    !string.IsNullOrWhiteSpace(
                        _editorLocator.FindEditorExecutable(diskVersion, _installedEditors)),
                    _installedEditors.Keys,
                    GetInstalledTargetPlatforms(diskVersion));
            }

            changed = true;
        }

        if (!changed)
        {
            return;
        }

        RebuildProjectFilterMenus();
        if (_sortCriteria == "EditorVersion"
            || _groupByMode == "EditorVersion"
            || _selectedEditorFilters.Count > 0)
        {
            ApplyFilterAndSort(refreshSourceControl: false);
        }
    }

    public void RefreshProjectVersionFromDisk(string projectPath, string version)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        var project = _allProjects.FirstOrDefault(candidate =>
            string.Equals(candidate.Path, projectPath, StringComparison.OrdinalIgnoreCase));
        if (project is null
            || string.Equals(project.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyAuthoritativeProjectVersions(
        [
            new UnityProjectInfo
            {
                Path = projectPath,
                Version = version
            }
        ]);
    }

    private void ApplyFilterAndSort(bool refreshSourceControl = true)
    {
        if (ProjectsListView is null || SearchBox is null || SummaryTextBlock is null || EmptyStatePanel is null)
        {
            return;
        }

        if (TagFilterSubItem is not null && TagFilterSubItem.Items.Count == 0)
        {
            RebuildTagFilterMenu();
        }

        var query = SearchBox.Text?.Trim() ?? string.Empty;

        IEnumerable<UnityProjectInfo> projects = _allProjects;
        if (!string.IsNullOrWhiteSpace(query))
        {
            projects = projects.Where(project =>
                project.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || project.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedTagFilters.Count > 0)
        {
            projects = projects.Where(project =>
                project.Tags.Any(tag => _selectedTagFilters.Contains(tag)));
        }

        if (_selectedEditorFilters.Count > 0)
        {
            projects = projects.Where(project => _selectedEditorFilters.Contains(project.Version));
        }

        if (_selectedPlatformFilters.Count > 0)
        {
            projects = projects.Where(project =>
                _selectedPlatformFilters.Contains(GetPlatformFilterLabel(project.BuildTarget)));
        }

        if (HideMissingProjectsItem?.IsChecked == true)
        {
            projects = projects.Where(p => System.IO.Directory.Exists(p.Path));
        }

        bool showMod = ShowModifiedColumnItem?.IsChecked ?? true;
        bool showVer = ShowEditorVersionColumnItem?.IsChecked ?? true;
        bool showPlatform = ShowPlatformColumnItem?.IsChecked ?? true;
        bool showFav = ShowFavoritesColumnItem?.IsChecked ?? true;
        bool showGit = ShowSourceControlColumnItem?.IsChecked ?? true;
        bool scEnabled = _settings.EnableSourceControl;

        foreach (var project in projects)
        {
            if (scEnabled && refreshSourceControl)
            {
                ApplySourceControlResult(project, SourceControlDetectionService.Detect(project));
            }
            else
            {
                if (!scEnabled)
                {
                    ApplySourceControlResult(project, null);
                }
            }
        }

        // Fallback sort criteria if current sorted column is hidden in display options
        if (_sortCriteria == "LastModified" && !showMod)
        {
            _sortCriteria = "Name";
            ShowStatus("Sorted by Name because the 'Modified' column was hidden.", InfoBarSeverity.Warning);
        }
        else if (_sortCriteria == "EditorVersion" && !showVer)
        {
            _sortCriteria = "Name";
            ShowStatus("Sorted by Name because the 'Editor version' column was hidden.", InfoBarSeverity.Warning);
        }
        else if (_sortCriteria == "Platform" && !showPlatform)
        {
            _sortCriteria = "Name";
            ShowStatus("Sorted by Name because the 'Platform' column was hidden.", InfoBarSeverity.Warning);
        }

        // Fallback top grouping criteria if current grouped column is hidden or disabled
        if (_keepSourceControlOnTop && (!showGit || !scEnabled))
        {
            _keepSourceControlOnTop = false;
            _keepStarredOnTop = showFav;
            ShowStatus("Top grouping switched to Starred because Source Control is hidden or disabled.", InfoBarSeverity.Warning);
        }
        else if (_keepStarredOnTop && !showFav)
        {
            _keepStarredOnTop = false;
            _keepSourceControlOnTop = false;
            ShowStatus("Top grouping reset because Favorites column is hidden.", InfoBarSeverity.Warning);
        }

        // Sort using canonical state fields (not flyout IsChecked which is unreliable)
        bool isName = _sortCriteria == "Name";
        bool isVersion = _sortCriteria == "EditorVersion";
        bool isPlatform = _sortCriteria == "Platform";

        if (isName)
        {
            projects = _sortAscending
                ? projects.OrderBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
                : projects.OrderByDescending(project => project.Title, StringComparer.CurrentCultureIgnoreCase);
        }
        else if (isVersion)
        {
            projects = _sortAscending
                ? projects.OrderBy(project => project.Version, StringComparer.OrdinalIgnoreCase)
                : projects.OrderByDescending(project => project.Version, StringComparer.OrdinalIgnoreCase);
        }
        else if (isPlatform)
        {
            projects = _sortAscending
                ? projects.OrderBy(project => GetPlatformFilterLabel(project.BuildTarget), StringComparer.CurrentCultureIgnoreCase)
                : projects.OrderByDescending(project => GetPlatformFilterLabel(project.BuildTarget), StringComparer.CurrentCultureIgnoreCase);
        }
        else // Default: Last modified
        {
            projects = _sortAscending
                ? projects.OrderBy(project => project.LastModifiedUtc)
                : projects.OrderByDescending(project => project.LastModifiedUtc);
        }

        if (KeepStarredOnTopItem?.IsChecked == true)
        {
            projects = projects
                .OrderByDescending(project => project.IsFavorite)
                .ThenBy(project => project, Comparer<UnityProjectInfo>.Create((left, right) =>
                {
                    int cmp;
                    if (isName)
                        cmp = string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
                    else if (isVersion)
                        cmp = string.Compare(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
                    else if (isPlatform)
                        cmp = string.Compare(
                            GetPlatformFilterLabel(left.BuildTarget),
                            GetPlatformFilterLabel(right.BuildTarget),
                            StringComparison.CurrentCultureIgnoreCase);
                    else
                        cmp = left.LastModifiedUtc.CompareTo(right.LastModifiedUtc);

                    return _sortAscending ? cmp : -cmp;
                }));
        }
        else if (_keepSourceControlOnTop)
        {
            projects = projects
                .OrderByDescending(project => !string.IsNullOrEmpty(project.SourceControlProvider))
                .ThenBy(project => project, Comparer<UnityProjectInfo>.Create((left, right) =>
                {
                    int cmp;
                    if (isName)
                        cmp = string.Compare(left.Title, right.Title, StringComparison.CurrentCultureIgnoreCase);
                    else if (isVersion)
                        cmp = string.Compare(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
                    else if (isPlatform)
                        cmp = string.Compare(
                            GetPlatformFilterLabel(left.BuildTarget),
                            GetPlatformFilterLabel(right.BuildTarget),
                            StringComparison.CurrentCultureIgnoreCase);
                    else
                        cmp = left.LastModifiedUtc.CompareTo(right.LastModifiedUtc);

                    return _sortAscending ? cmp : -cmp;
                }));
        }

        if (HeaderCol0 is not null) HeaderCol0.Width = new GridLength(showFav ? 36 : 0);
        if (HeaderCol1 is not null) HeaderCol1.Width = new GridLength((showGit && scEnabled) ? 36 : 0);
        if (HeaderCol3 is not null) HeaderCol3.Width = new GridLength(showMod ? 140 : 0);
        if (HeaderCol4 is not null) HeaderCol4.Width = new GridLength(showVer ? 130 : 0);
        if (HeaderCol5 is not null) HeaderCol5.Width = new GridLength(showPlatform ? 175 : 0);

        if (AddDropDownButton is not null) AddDropDownButton.Visibility = scEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (AddSimpleButton is not null) AddSimpleButton.Visibility = scEnabled ? Visibility.Collapsed : Visibility.Visible;
        if (ShowSourceControlColumnItem is not null) ShowSourceControlColumnItem.Visibility = scEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (SourceControlColumnHeaderButton is not null) SourceControlColumnHeaderButton.Visibility = (showGit && scEnabled) ? Visibility.Visible : Visibility.Collapsed;
        if (KeepSourceControlOnTopItem is not null) KeepSourceControlOnTopItem.Visibility = scEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (GroupBySourceControlItem is not null) GroupBySourceControlItem.Visibility = scEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (!scEnabled && _keepSourceControlOnTop)
        {
            _keepSourceControlOnTop = false;
            _keepStarredOnTop = true;
        }

        var editorVersions = _installedEditors.Keys.ToList();
        var viewModels = projects.Select(project =>
        {
            var isEditorInstalled = !_hasCompletedEditorDiscovery
                || !string.IsNullOrWhiteSpace(
                    _editorLocator.FindEditorExecutable(project.Version, _installedEditors));
            return new ProjectListItemViewModel(
                project,
                isEditorInstalled,
                editorVersions,
                GetInstalledTargetPlatforms(project.Version))
            {
                IsFavoriteColumnVisible = showFav,
                IsSourceControlColumnVisible = showGit && scEnabled,
                IsSourceControlEnabled = scEnabled,
                IsModifiedColumnVisible = showMod,
                IsEditorVersionColumnVisible = showVer,
                IsPlatformColumnVisible = showPlatform
            };
        }).ToList();

        _projectRows.Clear();
        foreach (var viewModel in viewModels)
        {
            _projectRows[viewModel.Project.Path] = viewModel;
        }

        if (_groupByMode == "None")
        {
            ProjectsListView.ItemsSource = viewModels;
        }
        else
        {
            var groups = BuildGroupViewModels(viewModels, _groupByMode);
            ProjectsCollectionViewSource.Source = groups;
            ProjectsListView.ItemsSource = ProjectsCollectionViewSource.View;
        }

        var filteredCount = viewModels.Count;
        SummaryTextBlock.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} {1}",
            filteredCount,
            filteredCount == 1 ? "project" : "projects");

        EmptyStatePanel.Visibility = filteredCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectsListView.Visibility = filteredCount == 0 ? Visibility.Collapsed : Visibility.Visible;

        SyncFlyoutToState();
        UpdateSortUIAndTooltips();
    }

    private void RebuildProjectFilterMenus()
    {
        if (EditorFilterSubItem is null || PlatformFilterSubItem is null || TagFilterSubItem is null)
        {
            return;
        }

        RebuildFilterMenu(
            EditorFilterSubItem,
            _allProjects
                .Select(project => project.Version)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase),
            _selectedEditorFilters,
            OnEditorFilterClick,
            OnClearEditorFiltersClick);

        RebuildFilterMenu(
            PlatformFilterSubItem,
            _allProjects
                .Select(project => GetPlatformFilterLabel(project.BuildTarget))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase),
            _selectedPlatformFilters,
            OnPlatformFilterClick,
            OnClearPlatformFiltersClick);

        RebuildTagFilterMenu();

        UpdateProjectFilterLabels();
    }

    private static void RebuildFilterMenu(
        MenuFlyoutSubItem submenu,
        IEnumerable<string> values,
        IReadOnlySet<string> selectedValues,
        RoutedEventHandler itemClickHandler,
        RoutedEventHandler clearClickHandler)
    {
        submenu.Items.Clear();

        var clearItem = new MenuFlyoutItem
        {
            Text = "Clear filter",
            IsEnabled = selectedValues.Count > 0
        };
        clearItem.Click += clearClickHandler;
        submenu.Items.Add(clearItem);
        submenu.Items.Add(new MenuFlyoutSeparator());

        var valueCount = 0;
        foreach (var value in values)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = value,
                Tag = value,
                IsChecked = selectedValues.Contains(value)
            };
            item.Click += itemClickHandler;
            submenu.Items.Add(item);
            valueCount++;
        }

        if (valueCount == 0)
        {
            submenu.Items.Add(new MenuFlyoutItem
            {
                Text = "No values available",
                IsEnabled = false
            });
        }
    }

    private void OnEditorFilterClick(object sender, RoutedEventArgs e)
    {
        UpdateFilterSelection(sender, _selectedEditorFilters);
    }

    private void OnPlatformFilterClick(object sender, RoutedEventArgs e)
    {
        UpdateFilterSelection(sender, _selectedPlatformFilters);
    }

    private void OnTagFilterClick(object sender, RoutedEventArgs e)
    {
        UpdateFilterSelection(sender, _selectedTagFilters);
    }

    private void UpdateFilterSelection(object sender, HashSet<string> selection)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string value } item)
        {
            return;
        }

        if (item.IsChecked)
        {
            selection.Add(value);
        }
        else
        {
            selection.Remove(value);
        }

        SaveProjectFilters();
        RebuildProjectFilterMenus();
        ApplyFilterAndSort(refreshSourceControl: false);
    }

    private void OnClearEditorFiltersClick(object sender, RoutedEventArgs e)
    {
        _selectedEditorFilters.Clear();
        SaveProjectFilters();
        RebuildProjectFilterMenus();
        ApplyFilterAndSort(refreshSourceControl: false);
    }

    private void OnClearPlatformFiltersClick(object sender, RoutedEventArgs e)
    {
        _selectedPlatformFilters.Clear();
        SaveProjectFilters();
        RebuildProjectFilterMenus();
        ApplyFilterAndSort(refreshSourceControl: false);
    }

    private void OnClearTagFiltersClick(object sender, RoutedEventArgs e)
    {
        _selectedTagFilters.Clear();
        SaveProjectFilters();
        RebuildProjectFilterMenus();
        ApplyFilterAndSort(refreshSourceControl: false);
    }

    private void SaveProjectFilters()
    {
        _settings.ProjectEditorFilters = _selectedEditorFilters
            .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.ProjectPlatformFilters = _selectedPlatformFilters
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.ProjectTagFilters = _selectedTagFilters
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settingsStore.Save(_settings);
        UpdateProjectFilterLabels();
    }

    private void UpdateProjectFilterLabels()
    {
        if (EditorFilterSubItem is not null)
        {
            EditorFilterSubItem.Text = _selectedEditorFilters.Count == 0
                ? "Filter by Editor version"
                : $"Editor version ({_selectedEditorFilters.Count})";
        }

        if (PlatformFilterSubItem is not null)
        {
            PlatformFilterSubItem.Text = _selectedPlatformFilters.Count == 0
                ? "Filter by platform"
                : $"Platform ({_selectedPlatformFilters.Count})";
        }

        if (TagFilterSubItem is not null)
        {
            TagFilterSubItem.Text = _selectedTagFilters.Count == 0
                ? "Filter by tag"
                : $"Tag ({_selectedTagFilters.Count})";
        }
    }

    public void FilterByEditorVersion(string editorVersion)
    {
        if (string.IsNullOrWhiteSpace(editorVersion))
        {
            return;
        }

        _selectedEditorFilters.Clear();
        _selectedEditorFilters.Add(editorVersion.Trim());

        _settings.ProjectEditorFilters = _selectedEditorFilters.ToList();
        _settingsStore.Save(_settings);

        RebuildProjectFilterMenus();
        ApplyFilterAndSort();
    }

    private static string GetPlatformFilterLabel(string? buildTarget)
        => buildTarget?.Trim() switch
        {
            null or "" => "Not set",
            "StandaloneWindows" or "StandaloneWindows64" => "Windows",
            "WindowsStoreApps" or "WSAPlayer" => "Universal Windows Platform",
            "StandaloneOSX" => "macOS",
            "StandaloneLinux64" => "Linux",
            "WebGL" or "WebGLPlayer" => "Web",
            "iPhone" or "iOS" => "iOS",
            var value => value
        };

    private static void ApplySourceControlResult(
        UnityProjectInfo project,
        SourceControlDetectionResult? sourceControl)
    {
        project.SourceControlProvider = sourceControl?.Provider;
        project.SourceControlDetail = sourceControl?.Branch;
        project.SourceControlRevision = sourceControl?.Revision;
        project.SourceControlRemoteUrl = sourceControl?.RemoteUrl;
        project.SourceControlRepository = sourceControl?.Repository;
        project.SourceControlHasRemote = sourceControl?.HasRemote ?? false;
        project.GitBranch = string.Equals(
                sourceControl?.Provider,
                SourceControlDetectionService.GitProvider,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                sourceControl?.Provider,
                SourceControlDetectionService.GitHubProvider,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                sourceControl?.Provider,
                SourceControlDetectionService.GitLabProvider,
                StringComparison.OrdinalIgnoreCase)
                ? sourceControl?.Branch
                : null;
    }

    /// <summary>
    /// Push _sortCriteria and _sortAscending into the flyout radio items.
    /// Guarded by _isSyncingFlyout to prevent re-entrant calls from Click events.
    /// </summary>
    private void SyncFlyoutToState()
    {
        SortLog($"[SORT] SyncFlyoutToState: criteria={_sortCriteria}, ascending={_sortAscending}");
        _isSyncingFlyout = true;
        try
        {
            if (SortByNameItem is not null) SortByNameItem.IsChecked = _sortCriteria == "Name";
            if (SortByLastModifiedItem is not null) SortByLastModifiedItem.IsChecked = _sortCriteria == "LastModified";
            if (SortByEditorVersionItem is not null) SortByEditorVersionItem.IsChecked = _sortCriteria == "EditorVersion";
            if (SortByPlatformItem is not null) SortByPlatformItem.IsChecked = _sortCriteria == "Platform";
            if (KeepStarredOnTopItem is not null) KeepStarredOnTopItem.IsChecked = _keepStarredOnTop;
            if (KeepSourceControlOnTopItem is not null) KeepSourceControlOnTopItem.IsChecked = _keepSourceControlOnTop;
            if (KeepNoneOnTopItem is not null) KeepNoneOnTopItem.IsChecked = !_keepStarredOnTop && !_keepSourceControlOnTop;
            if (SortAscendingItem is not null) SortAscendingItem.IsChecked = _sortAscending;
            if (SortDescendingItem is not null) SortDescendingItem.IsChecked = !_sortAscending;

            if (GroupByNoneItem is not null) GroupByNoneItem.IsChecked = _groupByMode == "None";
            if (GroupByFolderItem is not null) GroupByFolderItem.IsChecked = _groupByMode == "Folder";
            if (GroupBySourceControlItem is not null) GroupBySourceControlItem.IsChecked = _groupByMode == "SourceControl";
            if (GroupByEditorVersionItem is not null) GroupByEditorVersionItem.IsChecked = _groupByMode == "EditorVersion";

            // Visibility of Source Control options based on master toggle and display options
            bool scEnabled = _settings.EnableSourceControl;
            bool showGit = ShowSourceControlColumnItem?.IsChecked ?? true;

            if (KeepSourceControlOnTopItem is not null)
            {
                KeepSourceControlOnTopItem.Visibility = (scEnabled && showGit) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ShowSourceControlColumnItem is not null)
            {
                ShowSourceControlColumnItem.Visibility = scEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _isSyncingFlyout = false;
        }
        SortLog($"[SORT] SyncFlyoutToState DONE. Flyout: Name={SortByNameItem?.IsChecked}, LastMod={SortByLastModifiedItem?.IsChecked}, EdVer={SortByEditorVersionItem?.IsChecked}, Asc={SortAscendingItem?.IsChecked}, Desc={SortDescendingItem?.IsChecked}");
    }

    private void UpdateSortUIAndTooltips()
    {
        if (SortDropDownButton is null) return;

        string directionText = _sortAscending ? "Ascending" : "Descending";
        string currentGlyph = _sortAscending ? "\uE70E" : "\uE70D"; // ChevronUp / ChevronDown
        string hoveredName = _hoveredHeaderButton is null ? "null" : GetCriteriaForHeader(_hoveredHeaderButton) ?? "unknown";
        SortLog($"[SORT] UpdateSortUI: criteria={_sortCriteria}, ascending={_sortAscending}, glyph={(_sortAscending ? "UP" : "DOWN")}, hovered={hoveredName}");

        string criteriaText = _sortCriteria switch
        {
            "Name" => "Name",
            "EditorVersion" => "Editor version",
            "Platform" => "Platform",
            _ => "Last modified"
        };

        ToolTipService.SetToolTip(SortDropDownButton, $"Sort projects ({criteriaText}, {directionText})");

        // Name column: Glyph follows current sort mode, transparent until active or hovered
        if (NameColumnHeaderButton is not null && NameSortIcon is not null)
        {
            bool isActive = _sortCriteria == "Name";
            bool isHovered = ReferenceEquals(_hoveredHeaderButton, NameColumnHeaderButton);
            NameSortIcon.Glyph = currentGlyph;
            NameSortIcon.Visibility = Visibility.Visible;
            NameSortIcon.Opacity = (isActive || isHovered) ? 1.0 : 0.0;
            SortLog($"[SORT]   Name: active={isActive}, hovered={isHovered}, opacity={NameSortIcon.Opacity}");
            ToolTipService.SetToolTip(NameColumnHeaderButton, $"Sort by Name ({directionText})");
        }

        // Date modified column: Glyph follows current sort mode, transparent until active or hovered
        if (DateModifiedColumnHeaderButton is not null && DateModifiedSortIcon is not null)
        {
            bool isActive = _sortCriteria == "LastModified";
            bool isHovered = ReferenceEquals(_hoveredHeaderButton, DateModifiedColumnHeaderButton);
            DateModifiedSortIcon.Glyph = currentGlyph;
            DateModifiedSortIcon.Visibility = Visibility.Visible;
            DateModifiedSortIcon.Opacity = (isActive || isHovered) ? 1.0 : 0.0;
            SortLog($"[SORT]   DateMod: active={isActive}, hovered={isHovered}, opacity={DateModifiedSortIcon.Opacity}");
            ToolTipService.SetToolTip(DateModifiedColumnHeaderButton, $"Sort by Date modified ({directionText})");
        }

        // Editor version column: Glyph follows current sort mode, transparent until active or hovered
        if (EditorVersionColumnHeaderButton is not null && EditorVersionSortIcon is not null)
        {
            bool isActive = _sortCriteria == "EditorVersion";
            bool isHovered = ReferenceEquals(_hoveredHeaderButton, EditorVersionColumnHeaderButton);
            EditorVersionSortIcon.Glyph = currentGlyph;
            EditorVersionSortIcon.Visibility = Visibility.Visible;
            EditorVersionSortIcon.Opacity = (isActive || isHovered) ? 1.0 : 0.0;
            SortLog($"[SORT]   EdVer: active={isActive}, hovered={isHovered}, opacity={EditorVersionSortIcon.Opacity}");
            ToolTipService.SetToolTip(EditorVersionColumnHeaderButton, $"Sort by Unity editor ({directionText})");
        }

        // Platform column: Glyph follows current sort mode, transparent until active or hovered
        if (PlatformColumnHeaderButton is not null && PlatformSortIcon is not null)
        {
            bool isActive = _sortCriteria == "Platform";
            bool isHovered = ReferenceEquals(_hoveredHeaderButton, PlatformColumnHeaderButton);
            PlatformSortIcon.Glyph = currentGlyph;
            PlatformSortIcon.Visibility = Visibility.Visible;
            PlatformSortIcon.Opacity = (isActive || isHovered) ? 1.0 : 0.0;
            ToolTipService.SetToolTip(PlatformColumnHeaderButton, $"Sort by Platform ({directionText})");
        }

        if (FavoriteColumnHeaderButton is not null)
        {
            if (FavoriteHeaderIcon is not null)
            {
                FavoriteHeaderIcon.Visibility = _keepStarredOnTop
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            if (FilledFavoriteHeaderIcon is not null)
            {
                FilledFavoriteHeaderIcon.Visibility = _keepStarredOnTop
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            ToolTipService.SetToolTip(FavoriteColumnHeaderButton, _keepStarredOnTop ? "Keep starred on top (Enabled)" : "Keep starred on top (Disabled)");
        }

        if (SourceControlColumnHeaderButton is not null)
        {
            if (SourceControlHeaderIcon is not null)
            {
                SourceControlHeaderIcon.Foreground = _keepSourceControlOnTop
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }
            ToolTipService.SetToolTip(SourceControlColumnHeaderButton, _keepSourceControlOnTop ? "Keep source controlled on top (Enabled)" : "Keep source controlled on top (Disabled)");
        }
    }

    private FontIcon? GetSortIconForHeader(object header)
    {
        if (ReferenceEquals(header, NameColumnHeaderButton)) return NameSortIcon;
        if (ReferenceEquals(header, DateModifiedColumnHeaderButton)) return DateModifiedSortIcon;
        if (ReferenceEquals(header, EditorVersionColumnHeaderButton)) return EditorVersionSortIcon;
        if (ReferenceEquals(header, PlatformColumnHeaderButton)) return PlatformSortIcon;
        return null;
    }

    private string? GetCriteriaForHeader(object header)
    {
        if (ReferenceEquals(header, NameColumnHeaderButton)) return "Name";
        if (ReferenceEquals(header, DateModifiedColumnHeaderButton)) return "LastModified";
        if (ReferenceEquals(header, EditorVersionColumnHeaderButton)) return "EditorVersion";
        if (ReferenceEquals(header, PlatformColumnHeaderButton)) return "Platform";
        return null;
    }

    private void OnHeaderPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var name = GetCriteriaForHeader(sender) ?? "unknown";
        SortLog($"[SORT] PointerEntered: {name}");
        _hoveredHeaderButton = sender;
        var icon = GetSortIconForHeader(sender);
        if (icon is not null) icon.Opacity = 1.0;
    }

    private void OnHeaderPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var name = GetCriteriaForHeader(sender) ?? "unknown";
        SortLog($"[SORT] PointerExited: {name}, activeCriteria={_sortCriteria}");
        if (ReferenceEquals(_hoveredHeaderButton, sender))
        {
            _hoveredHeaderButton = null;
        }
        // Transparent when non-active; active column arrow stays visible (Opacity 1.0)
        if (NameSortIcon is not null && _sortCriteria != "Name")
            NameSortIcon.Opacity = 0.0;
        if (DateModifiedSortIcon is not null && _sortCriteria != "LastModified")
            DateModifiedSortIcon.Opacity = 0.0;
        if (EditorVersionSortIcon is not null && _sortCriteria != "EditorVersion")
            EditorVersionSortIcon.Opacity = 0.0;
        if (PlatformSortIcon is not null && _sortCriteria != "Platform")
            PlatformSortIcon.Opacity = 0.0;
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

    private void RebuildTagFilterMenu()
    {
        if (TagFilterSubItem is null) return;
        var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include standard Unity presets
        foreach (var tag in new[] { "Game", "Client Project", "Prototype", "Personal", "Simulation", "Archived", "Visualization", "Work in Progress", "2D", "3D" })
        {
            allTags.Add(tag);
        }

        // Include tags from loaded projects
        foreach (var proj in _allProjects)
        {
            foreach (var tag in proj.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                    allTags.Add(tag.Trim());
            }
        }

        // Include tags stored in settings
        foreach (var tagsList in _settings.ProjectTags.Values)
        {
            foreach (var tag in tagsList)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                    allTags.Add(tag.Trim());
            }
        }

        if (_selectedTagFilters.RemoveWhere(tag => !allTags.Contains(tag)) > 0)
        {
            SaveProjectFilters();
        }

        RebuildFilterMenu(
            TagFilterSubItem,
            allTags.OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase),
            _selectedTagFilters,
            OnTagFilterClick,
            OnClearTagFiltersClick);
    }

    private async void OnContextManageTagsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = (sender as FrameworkElement)?.Tag as ProjectListItemViewModel
                     ?? (sender as FrameworkElement)?.DataContext as ProjectListItemViewModel;

            if (vm is null)
            {
                ShowStatus("Unable to identify selected project for tag editing.", InfoBarSeverity.Error);
                return;
            }

            var allGlobalTags = _allProjects.SelectMany(p => p.Tags)
                .Concat(_settings.ProjectTags.Values.SelectMany(t => t))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var dialog = new ManageProjectTagsDialog(
                vm.Project,
                allGlobalTags,
                _settings.TagCategoryOrder)
            {
                XamlRoot = XamlRoot ?? Content?.XamlRoot,
                RequestedTheme = GetDialogTheme()
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var normalizedTags = UnityHubProjectService.NormalizeProjectTags(
                    dialog.SelectedTags.Select(t => t.Name));
                if (!_projectService.UpdateProjectTags(vm.Project.Path, normalizedTags))
                {
                    ShowStatus(
                        "Unable to save project tags to Unity Hub. Check that its project data file is available and try again.",
                        InfoBarSeverity.Error);
                    return;
                }

                vm.UpdateTags(normalizedTags);
                // The dialog persists color choices independently. Reload
                // before removing the legacy assignment so a stale page-level
                // settings snapshot cannot overwrite those local colors.
                var latestSettings = _settingsStore.Load();
                latestSettings.ProjectTags ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                latestSettings.ProjectTags.Remove(vm.Project.Path);
                latestSettings.TagCategoryOrder = dialog.AvailableTags
                    .Select(tag => tag.Name)
                    .ToList();
                _settingsStore.Save(latestSettings);
                _settings = latestSettings;
                RebuildTagFilterMenu();
                ApplyFilterAndSort(refreshSourceControl: false);
                ShowStatus($"Updated tags for '{vm.Project.Title}'.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to update project tags: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnContextLaunchPresetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement element && element.Tag is string presetName)
            {
                var vm = (element.DataContext as ProjectListItemViewModel)
                    ?? (element.Parent as FrameworkElement)?.DataContext as ProjectListItemViewModel;

                if (vm is not null)
                {
                    var preset = LaunchFlagPreset.BuiltInPresets.FirstOrDefault(p =>
                        p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
                    if (preset is not null)
                    {
                        var customArgs = string.IsNullOrWhiteSpace(vm.Project.CommandLineArguments)
                            ? preset.Flags
                            : $"{vm.Project.CommandLineArguments} {preset.Flags}".Trim();

                        TryOpenProject(vm.Project, customArgsOverride: customArgs);
                        ShowStatus($"Launching '{vm.Project.Title}' with {preset.Name} preset...", InfoBarSeverity.Success);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to launch project with preset: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Called when user clicks a radio item inside the sort flyout.
    /// Reads the flyout state into _sortCriteria/_sortAscending, then re-sorts.
    /// </summary>
    private void OnSortOptionClick(object sender, RoutedEventArgs e)
    {
        SortLog($"[SORT] OnSortOptionClick: isSyncing={_isSyncingFlyout}, sender={sender}");
        if (_isSyncingFlyout) return; // Ignore events fired during programmatic sync

        // Read which criteria radio is checked
        if (SortByNameItem?.IsChecked == true) _sortCriteria = "Name";
        else if (SortByEditorVersionItem?.IsChecked == true) _sortCriteria = "EditorVersion";
        else if (SortByPlatformItem?.IsChecked == true) _sortCriteria = "Platform";
        else _sortCriteria = "LastModified";

        // Read which top grouping radio is checked
        if (KeepStarredOnTopItem?.IsChecked == true)
        {
            _keepStarredOnTop = true;
            _keepSourceControlOnTop = false;
        }
        else if (KeepSourceControlOnTopItem?.IsChecked == true)
        {
            _keepStarredOnTop = false;
            _keepSourceControlOnTop = true;
        }
        else
        {
            _keepStarredOnTop = false;
            _keepSourceControlOnTop = false;
        }

        // Read which direction radio is checked
        _sortAscending = SortAscendingItem?.IsChecked == true;

        SortLog($"[SORT] OnSortOptionClick result: criteria={_sortCriteria}, starredTop={_keepStarredOnTop}, sourceTop={_keepSourceControlOnTop}, ascending={_sortAscending}");
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnKeepStarredColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        _keepStarredOnTop = !_keepStarredOnTop;
        if (_keepStarredOnTop)
        {
            _keepSourceControlOnTop = false;
        }
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnKeepSourceControlColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        _keepSourceControlOnTop = !_keepSourceControlOnTop;
        if (_keepSourceControlOnTop)
        {
            _keepStarredOnTop = false;
        }
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnNameColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        SortLog($"[SORT] NameHeaderClick: before criteria={_sortCriteria}, ascending={_sortAscending}");
        if (_sortCriteria == "Name")
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortCriteria = "Name";
            _sortAscending = true; // Default A to Z for Name
        }
        SortLog($"[SORT] NameHeaderClick: after criteria={_sortCriteria}, ascending={_sortAscending}");
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnDateModifiedColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        SortLog($"[SORT] DateModHeaderClick: before criteria={_sortCriteria}, ascending={_sortAscending}");
        if (_sortCriteria == "LastModified")
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortCriteria = "LastModified";
            _sortAscending = false; // Default Newest first for Date modified
        }
        SortLog($"[SORT] DateModHeaderClick: after criteria={_sortCriteria}, ascending={_sortAscending}");
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnEditorVersionColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        SortLog($"[SORT] EdVerHeaderClick: before criteria={_sortCriteria}, ascending={_sortAscending}");
        if (_sortCriteria == "EditorVersion")
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortCriteria = "EditorVersion";
            _sortAscending = false; // Default Highest version first for Editor version
        }
        SortLog($"[SORT] EdVerHeaderClick: after criteria={_sortCriteria}, ascending={_sortAscending}");
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnPlatformColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_sortCriteria == "Platform")
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortCriteria = "Platform";
            _sortAscending = true;
        }

        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void SaveDisplayAndSortSettings()
    {
        _settings.ShowFavoritesColumn = ShowFavoritesColumnItem?.IsChecked ?? true;
        _settings.ShowSourceControlColumn = ShowSourceControlColumnItem?.IsChecked ?? false;
        _settings.ShowModifiedColumn = ShowModifiedColumnItem?.IsChecked ?? true;
        _settings.ShowEditorVersionColumn = ShowEditorVersionColumnItem?.IsChecked ?? true;
        _settings.ShowPlatformColumn = ShowPlatformColumnItem?.IsChecked ?? true;
        _settings.HideMissingProjects = HideMissingProjectsItem?.IsChecked ?? false;

        _settings.SortCriteria = _sortCriteria;
        _settings.SortAscending = _sortAscending;
        _settings.KeepStarredOnTop = _keepStarredOnTop;
        _settings.KeepSourceControlOnTop = _keepSourceControlOnTop;

        _settingsStore.Save(_settings);
    }

    private async void OnAddProjectClick(object sender, RoutedEventArgs e)
    {
        await OpenAddProjectPickerAsync(sender as Control);
    }

    private async Task OpenAddProjectPickerAsync(Control? control)
    {
        try
        {
            if (control is not null) control.IsEnabled = false;

            var xamlRoot = control?.XamlRoot ?? Content?.XamlRoot;
            if (xamlRoot is null) return;
            var windowId = xamlRoot.ContentIslandEnvironment.AppWindowId;

            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId)
            {
                Title = "Add a Unity project",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                var folderPath = folder.Path;
                var projectTitle = Path.GetFileName(folderPath);
                var version = UnityHubProjectService.ParseProjectVersion(folderPath);

                _projectService.AddOrUpdateProject(folderPath, projectTitle, version);
                ReloadData(showSuccessMessage: false);
                ShowStatus($"Project '{projectTitle}' added to list (Unity {version}).", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to add project: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (control is not null) control.IsEnabled = true;
        }
    }

    private async void OnAddProjectFromRepositoryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!new AppSettingsStore().Load().EnableSourceControl)
            {
                return;
            }

            if (Content?.XamlRoot is null) return;

            var dialog = new FluenityHub_WinUIHost.Dialogs.AddProjectFromRepositoryDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = (Content.XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(dialog.DownloadedProjectPath))
            {
                var folderPath = dialog.DownloadedProjectPath;
                var projectTitle = Path.GetFileName(folderPath);
                var version = UnityHubProjectService.ParseProjectVersion(folderPath);

                _projectService.AddOrUpdateProject(folderPath, projectTitle, version);
                ReloadData(showSuccessMessage: false);
                ShowStatus($"Project '{projectTitle}' cloned and added to list (Unity {version}).", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnAddProjectFromRepositoryClick failed: {ex}");
            ShowStatus($"Unable to add project from repository: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var installedVersions = _editorLocator.GetInstalledEditors(_settings.CustomEditorPaths).Keys;
            if (!installedVersions.Any())
            {
                ShowStatus("No installed Unity Editors found. Install an editor before creating a project.", InfoBarSeverity.Warning);
                return;
            }

            var dialog = new FluenityHub_WinUIHost.Dialogs.NewProjectDialog(installedVersions)
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = (Content.XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(dialog.CreatedProjectPath))
            {
                _projectService.AddOrUpdateProject(dialog.CreatedProjectPath, dialog.CreatedProjectTitle, dialog.SelectedVersion);
                ReloadData(showSuccessMessage: false);
                ShowStatus($"New project '{dialog.CreatedProjectTitle}' created successfully.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnNewProjectClick failed: {ex}");
        }
    }

    private void OnDisplayOptionClick(object sender, RoutedEventArgs e)
    {
        SaveDisplayAndSortSettings();
        ApplyFilterAndSort();
    }

    private void OnReloadDataClick(object sender, RoutedEventArgs e)
    {
        ReloadData(showSuccessMessage: true);
    }

    private void OnProjectItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectListItemViewModel viewModel)
        {
            TryOpenProject(viewModel.Project);
        }
    }

    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        var selectedProject = GetSelectedProject();
        if (selectedProject is null)
        {
            ShowStatus("Select a project first.", InfoBarSeverity.Warning);
            return;
        }

        TryOpenProject(selectedProject);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var selectedProject = GetSelectedProject();
        if (selectedProject is null)
        {
            ShowStatus("Select a project first.", InfoBarSeverity.Warning);
            return;
        }

        OpenProjectFolder(selectedProject);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        var selectedProject = GetSelectedProject();
        if (selectedProject is null)
        {
            ShowStatus("Select a project first.", InfoBarSeverity.Warning);
            return;
        }

        CopyPathToClipboard(selectedProject.Path);
    }

    private void OnItemRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectListItemViewModel viewModel })
        {
            ProjectsListView.SelectedItem = viewModel;
        }
    }

    public async Task ShowVersionPickerDialog(UnityProjectInfo project)
    {
        try
        {
            if (!IsLoaded || XamlRoot is null)
            {
                var tcs = new TaskCompletionSource();
                RoutedEventHandler? loadedHandler = null;
                loadedHandler = (s, e) =>
                {
                    Loaded -= loadedHandler;
                    tcs.SetResult();
                };
                Loaded += loadedHandler;
                await tcs.Task;
            }

            await Task.Yield();

            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null) return;

            var targetTheme = (activeXamlRoot.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default;
            var dialog = new MissingEditorVersionDialog(project, _installedEditors)
            {
                XamlRoot = activeXamlRoot,
                RequestedTheme = targetTheme
            };
            var result = await dialog.ShowAsync();

            if (dialog.InstallOtherVersionRequested)
            {
                await OpenEditorInstallationFlowAsync(targetTheme);
                return;
            }

            if (result != ContentDialogResult.Primary || dialog.SelectedChoice is not { } choice)
            {
                return;
            }

            if (choice.RequiresInstallation)
            {
                await OpenEditorInstallationFlowAsync(targetTheme, choice.Version);
                return;
            }

            var executable = _editorLocator.FindEditorExecutable(choice.Version, _installedEditors);
            await LaunchProjectWithEditorAsync(
                project,
                executable,
                choice.Version,
                dialog.SelectedTargetPlatform);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShowVersionPickerDialog failed: {ex}");
            ShowStatus($"Unable to choose a Unity Editor: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task OpenEditorInstallationFlowAsync(
        ElementTheme targetTheme,
        string? requestedVersion = null)
    {
        UnityEditorRelease? release = null;
        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            try
            {
                release = await _editorReleaseService.GetReleaseAsync(requestedVersion);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to resolve Unity {requestedVersion}: {ex}");
            }
        }

        if (release is null)
        {
            var releasesDialog = new InstallEditorDialog(_installedEditors.Keys, requestedVersion)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = targetTheme
            };
            await releasesDialog.ShowAsync();
            release = releasesDialog.SelectedRelease;
        }

        if (release is null)
        {
            return;
        }

        var installRoot = _unityHubLocationSettingsService.GetInstallLocation();
        var modulesDialog = new AddModulesDialog(release, installRoot)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = targetTheme
        };
        await modulesDialog.ShowAsync();
        if (modulesDialog.InstallationRequest is null)
        {
            return;
        }

        var enqueueResult = _moduleInstallationManager.Enqueue(modulesDialog.InstallationRequest);
        ShowStatus(
            enqueueResult.Message,
            enqueueResult.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void OnEditorVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ProjectListItemViewModel viewModel } comboBox
            && comboBox.FocusState != FocusState.Unfocused
            && comboBox.SelectedItem is string selectedVersion
            && !string.IsNullOrWhiteSpace(selectedVersion))
        {
            if (string.Equals(
                    selectedVersion,
                    viewModel.SelectedEditorVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            viewModel.SelectedEditorVersion = selectedVersion;
            viewModel.RefreshAvailableTargetPlatforms(GetInstalledTargetPlatforms(selectedVersion));
            var project = viewModel.Project;
            _projectService.AddOrUpdateProject(
                project.Path,
                project.Title,
                selectedVersion,
                project.IsFavorite,
                buildTarget: viewModel.SelectedTargetPlatformId);
            ShowStatus($"Editor for '{project.Title}' changed to {selectedVersion}.", InfoBarSeverity.Success);
        }
    }

    private void OnTargetPlatformChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: ProjectListItemViewModel viewModel } comboBox
            && comboBox.FocusState != FocusState.Unfocused
            && comboBox.SelectedItem is TargetPlatformInfo selectedPlatform
            && !string.Equals(
                selectedPlatform.Id,
                viewModel.SelectedTargetPlatformId,
                StringComparison.OrdinalIgnoreCase))
        {
            viewModel.SelectedTargetPlatform = selectedPlatform;
            var project = viewModel.Project;
            _projectService.AddOrUpdateProject(
                project.Path,
                project.Title,
                project.Version,
                project.IsFavorite,
                buildTarget: selectedPlatform.Id);
            ShowStatus($"Platform for '{project.Title}' changed to {selectedPlatform.DisplayName}.", InfoBarSeverity.Success);
        }
    }

    private IReadOnlyList<TargetPlatformInfo> GetInstalledTargetPlatforms(string editorVersion)
    {
        if (_installedPlatforms.TryGetValue(editorVersion, out var exactMatch))
        {
            return exactMatch;
        }

        var matchingVersion = _installedPlatforms.Keys.FirstOrDefault(installedVersion =>
            installedVersion.StartsWith(editorVersion, StringComparison.OrdinalIgnoreCase)
            || editorVersion.StartsWith(installedVersion, StringComparison.OrdinalIgnoreCase));
        return matchingVersion is null ? [] : _installedPlatforms[matchingVersion];
    }


    private void OnToggleFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectListItemViewModel viewModel })
        {
            var project = viewModel.Project;
            project.IsFavorite = !project.IsFavorite;
            _projectService.AddOrUpdateProject(project.Path, project.Title, project.Version, project.IsFavorite);
            ApplyFilterAndSort();
        }
    }

    private void OnContextOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            TryOpenProject(viewModel.Project);
        }
    }

    private void OnContextOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            OpenProjectFolder(viewModel.Project);
        }
    }

    private void OnContextCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            CopyPathToClipboard(viewModel.Project.Path);
        }
    }

    private async void OnContextCopyShareLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            return;
        }

        var link = await CreateProjectShareLinkAsync(viewModel.Project);
        if (link is null)
        {
            return;
        }

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(link);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            ShowStatus("Link copied to clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copy project link failed: {ex}");
            ShowStatus("The link was created, but it could not be copied to the clipboard.", InfoBarSeverity.Error);
        }
    }

    private async void OnContextShareProjectLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            return;
        }

        var link = await CreateProjectShareLinkAsync(viewModel.Project);
        if (link is null)
        {
            return;
        }

        try
        {
            var window = MainWindow.Instance;
            if (window is null)
            {
                ShowStatus("The Windows share sheet is not available right now.", InfoBarSeverity.Error);
                return;
            }

            _windowsShareService.ShowLink(
                window.WindowHandle,
                viewModel.Project.Title,
                "Open this Unity project from its shared project link.",
                new Uri(link));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Share project link failed: {ex}");
            ShowStatus("The link was created, but the Windows share sheet could not be opened.", InfoBarSeverity.Error);
        }
    }

    private async Task<string?> CreateProjectShareLinkAsync(UnityProjectInfo project)
    {
        var result = await _projectShareLinkService.CreateAsync(project);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Link))
        {
            ShowStatus(result.Message, InfoBarSeverity.Error);
            return null;
        }

        return result.Link;
    }

    private void OnContextOpenTerminalClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            OpenTerminalAtProject(viewModel.Project.Path);
        }
    }

    private void OnOpenInIdeSubItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutSubItem subItem) return;

        var viewModel = subItem.DataContext as ProjectListItemViewModel;
        var projectPath = viewModel?.Project.Path;

        subItem.Items.Clear();
        var detectedIdes = _ideDetector.GetInstalledIdes();

        if (detectedIdes.Count == 0)
        {
            subItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No supported IDE detected",
                IsEnabled = false
            });
            return;
        }

        foreach (var ide in detectedIdes)
        {
            var item = new MenuFlyoutItem
            {
                Text = ide.Name,
                Icon = new FontIcon { Glyph = ide.Glyph },
                Tag = (ide.ExecutablePath, projectPath)
            };

            item.Click += (s, args) =>
            {
                if (s is MenuFlyoutItem { Tag: ValueTuple<string, string?> tag } && !string.IsNullOrEmpty(tag.Item2))
                {
                    if (!IdeDetector.LaunchIde(tag.Item1, tag.Item2))
                    {
                        ShowStatus($"Failed to launch {ide.Name}.", InfoBarSeverity.Error);
                    }
                }
            };

            subItem.Items.Add(item);
        }
    }

    private void OnOpenWithTargetPlatformSubItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutSubItem subItem) return;

        var viewModel = subItem.DataContext as ProjectListItemViewModel;
        if (viewModel is null) return;

        var project = viewModel.Project;
        var editorExecutable = _editorLocator.FindEditorExecutable(project.Version, _installedEditors);

        subItem.Items.Clear();

        if (string.IsNullOrEmpty(editorExecutable) || !File.Exists(editorExecutable))
        {
            subItem.Items.Add(new MenuFlyoutItem
            {
                Text = "Editor version not installed",
                IsEnabled = false
            });
            return;
        }

        var installedPlatforms = _editorLocator.GetInstalledTargetPlatforms(editorExecutable);

        foreach (var platform in installedPlatforms)
        {
            var item = new MenuFlyoutItem
            {
                Text = platform.DisplayName,
                Icon = new FontIcon { Glyph = platform.Glyph },
                Tag = platform.Id
            };

            item.Click += (s, args) =>
            {
                TryOpenProject(project, platform.Id);
            };

            subItem.Items.Add(item);
        }
    }

    private async void OnContextSaveAsTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var project = (sender as FrameworkElement)?.Tag is ProjectListItemViewModel vm
                ? vm.Project
                : (sender as FrameworkElement)?.DataContext as UnityProjectInfo;

            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null) return;

            var dialog = new SaveProjectAsTemplateDialog(_allProjects, _installedEditors, project?.Path)
            {
                XamlRoot = activeXamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && dialog.ResultTemplate is not null)
            {
                ShowStatus($"Custom template '{dialog.ResultTemplate.Name}' created successfully!", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to create template: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void OnContextOpenEditorLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var project = (sender as FrameworkElement)?.Tag is ProjectListItemViewModel vm ? vm.Project : null;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var editorLogPath = Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            if (project is not null)
            {
                var projectLogPath = Path.Combine(project.Path, "Logs", "Editor.log");
                if (File.Exists(projectLogPath))
                {
                    editorLogPath = projectLogPath;
                }
            }

            if (!File.Exists(editorLogPath))
            {
                ShowStatus("No Unity Editor.log file found.", InfoBarSeverity.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = editorLogPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to open Editor.log: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnContextCommandLineArgumentsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel }) return;
            var project = viewModel.Project;
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null) return;

            var dialog = new FluenityHub_WinUIHost.Dialogs.CommandLineArgumentsDialog(project)
            {
                XamlRoot = activeXamlRoot,
                RequestedTheme = (activeXamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _projectService.UpdateProjectCommandLineArguments(project.Path, dialog.SavedArguments);
                project.CommandLineArguments = string.IsNullOrWhiteSpace(dialog.SavedArguments) ? null : dialog.SavedArguments;
                ShowStatus($"Updated command line arguments for '{project.Title}'.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to update command line arguments: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnContextCleanCacheClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel }) return;
            var project = viewModel.Project;
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null) return;

            var dialog = new FluenityHub_WinUIHost.Dialogs.ProjectCleanupDialog(project.Title, project.Path)
            {
                XamlRoot = activeXamlRoot,
                RequestedTheme = (activeXamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (dialog.DeletedFolderCount > 0)
                {
                    ShowStatus(
                        $"Cleaned {FormatBytes(dialog.TotalFreedBytes)} from {dialog.DeletedFolderCount} folder(s) for '{project.Title}'.",
                        InfoBarSeverity.Success);
                }
                else
                {
                    ShowStatus($"No selected cleanup folders were found for '{project.Title}'.", InfoBarSeverity.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to clean project files: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnContextBackupProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            return;
        }

        var project = viewModel.Project;
        if (!CanStartProjectCopy(project))
        {
            return;
        }

        try
        {
            var dialog = CreateProjectCopyDialog(
                project,
                ProjectCopyMode.Backup,
                async (request, progress, cancellationToken) =>
                {
                    await _projectBackupService.CreateBackupAsync(
                        project,
                        request.TargetPath,
                        request.IncludeUserSettings,
                        request.IncludeGitHistory,
                        progress,
                        cancellationToken);
                });
            await dialog.ShowAsync();
            if (!dialog.OperationCompleted)
            {
                return;
            }

            ShowStatus(
                $"Backup created for '{project.Title}' in '{dialog.TargetPath}'.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Backup failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnContextCloneProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            return;
        }

        var project = viewModel.Project;
        if (!CanStartProjectCopy(project))
        {
            return;
        }

        try
        {
            var dialog = CreateProjectCopyDialog(
                project,
                ProjectCopyMode.Clone,
                async (request, progress, cancellationToken) =>
                {
                    await _projectBackupService.CloneProjectAsync(
                        project.Path,
                        request.TargetPath,
                        request.IncludeUserSettings,
                        request.IncludeGitHistory,
                        progress,
                        cancellationToken);
                });
            await dialog.ShowAsync();
            if (!dialog.OperationCompleted)
            {
                return;
            }

            AddCopiedProjectToList(dialog.TargetPath);
            ReloadData(showSuccessMessage: false);
            ShowStatus(
                $"Project cloned to '{dialog.TargetPath}' and added to the project list.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus($"Clone failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnContextManageBackupsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            return;
        }

        var project = viewModel.Project;
        try
        {
            while (true)
            {
                var dialog = new ProjectBackupsDialog(
                    project.Title,
                    project.Path,
                    _projectBackupService)
                {
                    XamlRoot = GetActiveXamlRoot(),
                    RequestedTheme = GetDialogTheme()
                };

                await dialog.ShowAsync();
                if (dialog.SelectedBackup is null
                    || dialog.RequestedAction == ProjectBackupDialogAction.None)
                {
                    return;
                }

                if (dialog.RequestedAction == ProjectBackupDialogAction.Delete)
                {
                    if (!await ConfirmDeleteBackupAsync(dialog.SelectedBackup))
                    {
                        continue;
                    }

                    var deletedExistingBackup = dialog.SelectedBackup.IsAvailable;
                    await _projectBackupService.DeleteBackupAsync(dialog.SelectedBackup);
                    ShowStatus(
                        deletedExistingBackup
                            ? "Backup deleted permanently."
                            : "Missing backup record removed.",
                        InfoBarSeverity.Success);
                    continue;
                }

                await RestoreBackupAsync(project, dialog.SelectedBackup);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Restore canceled.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus($"Backup operation failed: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async Task RestoreBackupAsync(
        UnityProjectInfo project,
        ProjectBackupRecord backup)
    {
        var dialog = CreateProjectCopyDialog(
            project,
            ProjectCopyMode.Restore,
            async (request, progress, cancellationToken) =>
            {
                await _projectBackupService.RestoreBackupAsNewProjectAsync(
                    backup,
                    request.TargetPath,
                    request.IncludeGitHistory,
                    progress,
                    cancellationToken);
            },
            backup);
        await dialog.ShowAsync();
        if (!dialog.OperationCompleted)
        {
            return;
        }

        AddCopiedProjectToList(dialog.TargetPath);
        ReloadData(showSuccessMessage: false);
        ShowStatus(
            $"Backup restored to '{dialog.TargetPath}' and added to the project list.",
            InfoBarSeverity.Success);
    }

    private ProjectCopyDialog CreateProjectCopyDialog(
        UnityProjectInfo project,
        ProjectCopyMode mode,
        Func<ProjectCopyRequest, IProgress<ProjectCopyProgress>, CancellationToken, Task> operation,
        ProjectBackupRecord? backup = null)
        => new(project, mode, operation, backup)
        {
            XamlRoot = GetActiveXamlRoot(),
            RequestedTheme = GetDialogTheme()
        };

    private XamlRoot GetActiveXamlRoot()
        => XamlRoot ?? Content?.XamlRoot
           ?? throw new InvalidOperationException("The project page is not attached to a window.");

    private ElementTheme GetDialogTheme()
    {
        var root = GetActiveXamlRoot();
        return (root.Content as FrameworkElement)?.RequestedTheme
               ?? MainWindow.Instance?.CurrentTheme
               ?? ElementTheme.Default;
    }

    private bool CanStartProjectCopy(UnityProjectInfo project)
    {
        if (!Directory.Exists(project.Path))
        {
            ShowStatus("The project folder no longer exists.", InfoBarSeverity.Error);
            return false;
        }

        if (UnityProcessService.IsProjectInUse(project.Path))
        {
            ShowStatus(
                $"Close '{project.Title}' in Unity before creating a consistent backup or clone.",
                InfoBarSeverity.Warning);
            return false;
        }

        return true;
    }

    private void AddCopiedProjectToList(string projectPath)
    {
        var projectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath));
        var version = UnityHubProjectService.ParseProjectVersion(projectPath);
        _projectService.AddOrUpdateProject(
            projectPath,
            projectName,
            version,
            isFavorite: false,
            hasCustomDisplayName: false);
    }

    private async Task<bool> ConfirmDeleteBackupAsync(ProjectBackupRecord backup)
    {
        var title = backup.IsAvailable
            ? "Delete this backup permanently?"
            : "Remove this missing backup record?";
        var content = backup.IsAvailable
            ? $"'{backup.BackupPath}' and all files inside it will be permanently deleted. The original project will not be affected."
            : $"FluenityHub can no longer find '{backup.BackupPath}'. Removing this record will not delete any files.";
        var primaryText = backup.IsAvailable ? "Delete permanently" : "Remove record";
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = GetActiveXamlRoot(),
            RequestedTheme = GetDialogTheme()
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnContextRenameProjectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: ProjectListItemViewModel viewModel })
            {
                return;
            }

            var project = viewModel.Project;
            var nameInput = new TextBox
            {
                Text = project.Title,
                PlaceholderText = "Enter display name",
                SelectionStart = 0,
                SelectionLength = project.Title.Length
            };

            var dialog = new ContentDialog
            {
                Title = "Rename project",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Set a display name for this project. The directory name and any Unity Cloud connected projects will remain unchanged.",
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                        },
                        nameInput
                    }
                },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = (Content.XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var newName = nameInput.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(newName) && newName != project.Title)
                {
                    _projectService.AddOrUpdateProject(
                        project.Path,
                        newName,
                        project.Version,
                        project.IsFavorite,
                        hasCustomDisplayName: true);
                    ReloadData(showSuccessMessage: false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnContextRenameProjectClick failed: {ex}");
        }
    }

    private async void OnContextConnectSourceControlClick(object sender, RoutedEventArgs e)
    {
        if (!new AppSettingsStore().Load().EnableSourceControl)
        {
            return;
        }

        if (sender is FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            try
            {
                var project = viewModel.Project;
                if (viewModel.IsGitBackedSourceControl)
                {
                    var dialog = new ContentDialog
                    {
                        Title = $"Disconnect from {project.SourceControlProvider}?",
                        Content = $"FluenityHub will stop showing source-control integration for '{project.Title}'. The local Git repository, history, and remote configuration will be kept.",
                        PrimaryButtonText = "Disconnect",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = XamlRoot ?? Content?.XamlRoot,
                        RequestedTheme = (XamlRoot?.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        _projectService.DisconnectProjectFromSourceControl(project.Path);
                        ReloadData(showSuccessMessage: false);
                        ShowStatus($"Disconnected '{project.Title}' from {project.SourceControlProvider}. Git files were kept.", InfoBarSeverity.Success);
                    }
                }
                else
                {
                    var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
                    if (activeXamlRoot is null) return;

                    var dialog = new ConnectSourceControlDialog(project)
                    {
                        XamlRoot = activeXamlRoot,
                        RequestedTheme = (activeXamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        _projectService.ConnectProjectToSourceControl(
                            project.Path,
                            dialog.SelectedProvider,
                            dialog.OrganizationName,
                            dialog.RepositoryName);

                        ShowStatus($"Successfully connected '{project.Title}' to {dialog.SelectedProvider.ToUpperInvariant()}!", InfoBarSeverity.Success);
                        ReloadData(showSuccessMessage: false);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowStatus($"Source control operation failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private void OnContextRemoveProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectListItemViewModel viewModel })
        {
            ConfirmAndRemoveProject(viewModel.Project);
        }
    }



    private void OnNewProjectAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnNewProjectClick(sender, new RoutedEventArgs());
    }

    private void OnRefreshAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnReloadDataClick(sender, new RoutedEventArgs());
    }

    private async void ConfirmAndRemoveProject(UnityProjectInfo project)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Remove project from list?",
                Content = $"Are you sure you want to remove '{project.Title}' from your recent projects list? This will not delete any files on disk.",
                PrimaryButtonText = "Remove",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
                RequestedTheme = (Content.XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _projectService.RemoveProject(project.Path);
                ReloadData(showSuccessMessage: false);
                ShowStatus($"Project '{project.Title}' removed from list.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ConfirmAndRemoveProject failed: {ex}");
        }
    }

    private void OpenTerminalAtProject(string projectPath)
    {
        if (!Directory.Exists(projectPath))
        {
            ShowStatus($"Project folder not found: {projectPath}", InfoBarSeverity.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = $"-d \"{projectPath}\"",
                UseShellExecute = true
            });
            ShowStatus("Terminal launched.", InfoBarSeverity.Success);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    WorkingDirectory = projectPath,
                    UseShellExecute = true
                });
                ShowStatus("PowerShell launched.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"Unable to launch terminal: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private void CopyPathToClipboard(string path)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            ShowStatus("Folder path copied to clipboard.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CopyPathToClipboard failed: {ex}");
        }
    }

    private void OpenProjectFolder(UnityProjectInfo project)
    {
        if (!Directory.Exists(project.Path))
        {
            ShowStatus($"Project folder not found: {project.Path}", InfoBarSeverity.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = project.Path,
                UseShellExecute = true
            });
            ShowStatus("Project folder opened in File Explorer.", InfoBarSeverity.Success);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = project.Path,
                    UseShellExecute = true
                });
                ShowStatus("Project folder opened in File Explorer.", InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus($"Unable to open folder: {ex.Message}", InfoBarSeverity.Error);
            }
        }
    }

    private void OnProjectsListViewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var selectedProject = GetSelectedProject();
        if (selectedProject is null) return;

        var stateCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        var stateShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        bool isCtrl = stateCtrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool isShift = stateShift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            TryOpenProject(selectedProject);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.C && isCtrl && isShift)
        {
            CopyPathToClipboard(selectedProject.Path);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Delete)
        {
            ConfirmAndRemoveProject(selectedProject);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.F && isCtrl)
        {
            OpenProjectFolder(selectedProject);
            e.Handled = true;
        }
    }

    private UnityProjectInfo? GetSelectedProject()
    {
        return (ProjectsListView.SelectedItem as ProjectListItemViewModel)?.Project;
    }

    private void OnContextOpenTargetPlatformClick(object sender, RoutedEventArgs e)
    {
        var targetPlatform = (sender as FrameworkElement)?.Tag as string;
        var vm = (sender as FrameworkElement)?.DataContext as ProjectListItemViewModel
                 ?? (sender as FrameworkElement)?.Tag as ProjectListItemViewModel;

        if (!string.IsNullOrEmpty(targetPlatform) && vm is not null)
        {
            TryOpenProject(vm.Project, targetPlatform);
        }
        else if (!string.IsNullOrEmpty(targetPlatform))
        {
            var selectedProject = GetSelectedProject();
            if (selectedProject is not null)
            {
                TryOpenProject(selectedProject, targetPlatform);
            }
        }
    }

    private void TryOpenProject(UnityProjectInfo project, string? targetPlatform = null, string? customArgsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(targetPlatform)
            && _projectRows.TryGetValue(project.Path, out var row))
        {
            targetPlatform = row.SelectedTargetPlatformId;
        }

        if (!Directory.Exists(project.Path))
        {
            ShowStatus($"Project folder not found: {project.Path}", InfoBarSeverity.Error);
            return;
        }

        if (UnityProcessService.TryFocusRunningProject(project.Path))
        {
            return;
        }

        var editorExecutable = _editorLocator.FindEditorExecutable(project.Version, _installedEditors);
        if (string.IsNullOrWhiteSpace(editorExecutable))
        {
            _ = ShowVersionPickerDialog(project);
            return;
        }

        _ = LaunchProjectWithEditorAsync(
            project,
            editorExecutable,
            project.Version,
            targetPlatform,
            customArgsOverride);
    }

    private async Task LaunchProjectWithEditorAsync(
        UnityProjectInfo project,
        string? editorExecutable,
        string editorVersion,
        string? targetPlatform = null,
        string? customArgsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(editorExecutable) || !File.Exists(editorExecutable))
        {
            ShowStatus($"Unity {editorVersion} is no longer available at its registered location.", InfoBarSeverity.Error);
            return;
        }

        var extraArgs = customArgsOverride ?? project.CommandLineArguments?.Trim();
        var result = await _editorLaunchService.LaunchProjectAsync(
            editorExecutable,
            project.Path,
            editorVersion,
            targetPlatform,
            extraArgs);
        if (!result.Succeeded)
        {
            ShowStatus(result.Message, InfoBarSeverity.Error);
            return;
        }

        MainWindow.Instance?.NotifyEditorLaunched(result.EditorProcess, project.Path);
        if (!string.IsNullOrEmpty(targetPlatform))
        {
            ShowStatus(
                $"Launching Unity ({editorVersion}) for platform '{targetPlatform}'...",
                InfoBarSeverity.Success);
        }
    }

    public void OpenExternalProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(projectPath.Trim().Trim('"'));
        var project = _allProjects.FirstOrDefault(candidate =>
            string.Equals(
                Path.GetFullPath(candidate.Path),
                normalizedPath,
                StringComparison.OrdinalIgnoreCase));

        if (project is null)
        {
            var versionFile = Path.Combine(normalizedPath, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(versionFile))
            {
                ShowStatus("The selected folder is not a Unity project.", InfoBarSeverity.Warning);
                return;
            }

            var versionLine = File.ReadLines(versionFile)
                .FirstOrDefault(line => line.StartsWith("m_EditorVersion:", StringComparison.OrdinalIgnoreCase));
            var version = versionLine?.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty;
            var title = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedPath));
            _projectService.AddOrUpdateProject(normalizedPath, title, version);
            ReloadData(showSuccessMessage: false);
            project = _allProjects.FirstOrDefault(candidate =>
                string.Equals(
                    Path.GetFullPath(candidate.Path),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (project is not null)
        {
            TryOpenProject(project);
        }
    }

    private CancellationTokenSource? _infoBarCts;

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int digitGroup = (int)(Math.Log10(bytes) / Math.Log10(1024));
        digitGroup = Math.Clamp(digitGroup, 0, units.Length - 1);
        double number = bytes / Math.Pow(1024, digitGroup);
        return $"{number:0.##} {units[digitGroup]}";
    }

    private async void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (StatusInfoBar is null)
        {
            return;
        }

        _infoBarCts?.Cancel();
        _infoBarCts = new CancellationTokenSource();
        var token = _infoBarCts.Token;

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

        try
        {
            await Task.Delay(4000, token);
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

    private void OnGroupByOptionClick(object sender, RoutedEventArgs e)
    {
        if (_isSyncingFlyout) return;

        if (sender is RadioMenuFlyoutItem item && item.IsChecked)
        {
            if (item == GroupByNoneItem) _groupByMode = "None";
            else if (item == GroupByFolderItem) _groupByMode = "Folder";
            else if (item == GroupBySourceControlItem) _groupByMode = "SourceControl";
            else if (item == GroupByEditorVersionItem) _groupByMode = "EditorVersion";

            ApplyFilterAndSort(refreshSourceControl: false);
        }
    }

    private List<ProjectGroupViewModel> BuildGroupViewModels(List<ProjectListItemViewModel> viewModels, string mode)
    {
        List<ProjectGroupViewModel> result = [];

        if (mode == "Folder")
        {
            var grouped = viewModels
                .GroupBy(vm => vm.Group)
                .OrderBy(g => string.Equals(g.Key, "Ungrouped", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var g in grouped)
            {
                result.Add(CreateProjectGroupViewModel(g.Key, "\uE8B7", g));
            }
        }
        else if (mode == "SourceControl")
        {
            var grouped = viewModels
                .GroupBy(vm => vm.HasSourceControl ? vm.SourceControlLabel : "No Source Control")
                .OrderBy(g => string.Equals(g.Key, "No Source Control", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var g in grouped)
            {
                result.Add(CreateProjectGroupViewModel(g.Key, "\uE71B", g));
            }
        }
        else if (mode == "EditorVersion")
        {
            var grouped = viewModels
                .GroupBy(vm => string.IsNullOrWhiteSpace(vm.VersionLabel) ? "Unknown Version" : vm.VersionLabel)
                .OrderByDescending(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in grouped)
            {
                result.Add(CreateProjectGroupViewModel(g.Key, "\uE74C", g));
            }
        }

        return result;
    }

    private ProjectGroupViewModel CreateProjectGroupViewModel(
        string key,
        string headerGlyph,
        IEnumerable<ProjectListItemViewModel> items)
    {
        return new ProjectGroupViewModel(
            key,
            headerGlyph,
            items,
            _collapsedProjectGroupKeys.Contains(GetProjectGroupStateKey(key)));
    }

    private string GetProjectGroupStateKey(string groupKey)
    {
        return string.Concat(_groupByMode, "\u001F", groupKey);
    }

    private void SetProjectGroupCollapsed(ProjectGroupViewModel group, bool isCollapsed)
    {
        group.SetCollapsed(isCollapsed);
        var stateKey = GetProjectGroupStateKey(group.Key);
        if (isCollapsed)
        {
            _collapsedProjectGroupKeys.Add(stateKey);
        }
        else
        {
            _collapsedProjectGroupKeys.Remove(stateKey);
        }
    }

    private IEnumerable<ProjectGroupViewModel> GetCurrentProjectGroups()
    {
        return ProjectsCollectionViewSource.Source as IEnumerable<ProjectGroupViewModel>
            ?? [];
    }

    private void ToggleProjectGroup(ProjectGroupViewModel group)
    {
        var isExpanding = group.IsCollapsed;
        SetProjectGroupCollapsed(group, !group.IsCollapsed);

        if (!isExpanding || group.Count == 0)
        {
            return;
        }

        // Let the grouped collection Reset reach the ListView before asking its
        // virtualization panel to reveal the first child. ScrollIntoView uses the
        // control's native minimal-scroll behavior, so a pinned header stays put
        // unless there is no room to show any of the newly expanded rows.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (!group.IsCollapsed && group.Count > 0)
                {
                    ProjectsListView.ScrollIntoView(
                        group[0],
                        ScrollIntoViewAlignment.Default);
                }
            });
    }

    private void OnProjectGroupChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectGroupViewModel group })
        {
            ToggleProjectGroup(group);
        }
    }

    private void OnProjectGroupHeaderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectGroupViewModel group })
        {
            e.Handled = true;
            ToggleProjectGroup(group);
        }
    }

    private void OnProjectGroupToggleMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectGroupViewModel group })
        {
            ToggleProjectGroup(group);
        }
    }

    private void OnExpandAllProjectGroupsClick(object sender, RoutedEventArgs e)
    {
        foreach (var group in GetCurrentProjectGroups().ToList())
        {
            SetProjectGroupCollapsed(group, false);
        }
    }

    private void OnCollapseAllProjectGroupsClick(object sender, RoutedEventArgs e)
    {
        foreach (var group in GetCurrentProjectGroups().ToList())
        {
            SetProjectGroupCollapsed(group, true);
        }
    }

    private void OnMoveToGroupSubItemLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutSubItem subItem || subItem.DataContext is not ProjectListItemViewModel vm)
        {
            return;
        }

        subItem.Items.Clear();

        var existingGroups = _allProjects
            .Select(p => p.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g) && !string.Equals(g, "Ungrouped", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var groupName in existingGroups)
        {
            var item = new MenuFlyoutItem
            {
                Text = groupName
            };
            if (string.Equals(vm.Group, groupName, StringComparison.OrdinalIgnoreCase))
            {
                item.Icon = new FontIcon { Glyph = "\uE73E" };
            }

            item.Click += (s, args) =>
            {
                vm.UpdateGroup(groupName);
                _projectService.UpdateProjectGroup(vm.Path, groupName);
                ShowStatus($"Moved '{vm.Title}' to group '{groupName}'.", InfoBarSeverity.Success);
                ApplyFilterAndSort(refreshSourceControl: false);
            };

            subItem.Items.Add(item);
        }

        if (existingGroups.Count > 0)
        {
            subItem.Items.Add(new MenuFlyoutSeparator());
        }

        var newGroupItem = new MenuFlyoutItem
        {
            Text = "New group...",
            Icon = new FontIcon { Glyph = "\uE710" }
        };
        newGroupItem.Click += async (s, args) =>
        {
            await PromptAndCreateNewGroupAsync(vm);
        };
        subItem.Items.Add(newGroupItem);

        if (!string.Equals(vm.Group, "Ungrouped", StringComparison.OrdinalIgnoreCase))
        {
            var ungroupItem = new MenuFlyoutItem
            {
                Text = "Remove from group",
                Icon = new FontIcon { Glyph = "\uE711" }
            };
            ungroupItem.Click += (s, args) =>
            {
                vm.UpdateGroup("Ungrouped");
                _projectService.UpdateProjectGroup(vm.Path, "Ungrouped");
                ShowStatus($"Removed '{vm.Title}' from group.", InfoBarSeverity.Success);
                ApplyFilterAndSort(refreshSourceControl: false);
            };
            subItem.Items.Add(ungroupItem);
        }
    }

    private async Task PromptAndCreateNewGroupAsync(ProjectListItemViewModel vm)
    {
        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null) return;

            var inputTextBox = new TextBox
            {
                Header = "Group name",
                PlaceholderText = "e.g. Client Work, Game Jams",
                Margin = new Thickness(0, 8, 0, 0)
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(inputTextBox, "Group name");

            var dialog = new ContentDialog
            {
                Title = $"Move '{vm.Title}' to new group",
                Content = inputTextBox,
                PrimaryButtonText = "Create & Move",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = activeXamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(inputTextBox.Text))
            {
                var newGroupName = inputTextBox.Text.Trim();
                vm.UpdateGroup(newGroupName);
                _projectService.UpdateProjectGroup(vm.Path, newGroupName);
                ShowStatus($"Moved '{vm.Title}' to new group '{newGroupName}'.", InfoBarSeverity.Success);

                _groupByMode = "Folder";
                if (GroupByFolderItem is not null) GroupByFolderItem.IsChecked = true;
                ApplyFilterAndSort(refreshSourceControl: false);
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to create group: {ex.Message}", InfoBarSeverity.Error);
        }
    }
}

public sealed class ProjectGroupViewModel : System.Collections.ObjectModel.ObservableCollection<ProjectListItemViewModel>
{
    private readonly IReadOnlyList<ProjectListItemViewModel> _allItems;
    private bool _isCollapsed;

    public string Key { get; }
    public string HeaderGlyph { get; }
    public string CountLabel => $"{_allItems.Count}";
    public bool IsCollapsed => _isCollapsed;
    public string ChevronGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public string ToggleLabel => IsCollapsed ? "Expand" : "Collapse";
    public string ToggleAutomationName => $"{ToggleLabel} {Key}";

    public ProjectGroupViewModel(
        string key,
        string headerGlyph,
        IEnumerable<ProjectListItemViewModel> items,
        bool isCollapsed)
        : this(key, headerGlyph, items.ToList(), isCollapsed)
    {
    }

    private ProjectGroupViewModel(
        string key,
        string headerGlyph,
        IReadOnlyList<ProjectListItemViewModel> items,
        bool isCollapsed)
        : base(isCollapsed ? [] : items)
    {
        Key = key;
        HeaderGlyph = headerGlyph;
        _allItems = items;
        _isCollapsed = isCollapsed;
    }

    public void SetCollapsed(bool isCollapsed)
    {
        if (_isCollapsed == isCollapsed)
        {
            return;
        }

        _isCollapsed = isCollapsed;
        CheckReentrancy();
        Items.Clear();
        if (!isCollapsed)
        {
            foreach (var item in _allItems)
            {
                Items.Add(item);
            }
        }

        // Publish the entire expanded/collapsed state as one update. Raising one
        // Add notification per project makes ItemsStackPanel repeatedly recalculate
        // its anchor and can leave a newly expanded group's rows outside the viewport.
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCollapsed)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ChevronGlyph)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ToggleLabel)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ToggleAutomationName)));
    }
}
