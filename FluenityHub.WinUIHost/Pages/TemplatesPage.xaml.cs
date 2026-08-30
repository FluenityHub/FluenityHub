using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Dialogs;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluenityHub_WinUIHost.Pages;

public sealed partial class TemplatesPage : Page
{
    private readonly TemplateService _templateService = new();
    private readonly UnityHubProjectService _projectService = new();
    private readonly UnityEditorLocator _editorLocator = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly UnityEditorReleaseService _releaseService = new();

    private List<CustomTemplateInfo> _allTemplates = [];
    private List<UnityProjectInfo> _allProjects = [];
    private Dictionary<string, string> _installedEditors = [];
    private AppSettings _settings = new();
    private string _sortCriteria = "CreatedAt";
    private bool _sortAscending;
    private HashSet<string> _selectedEditorFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedTagFilters = new(StringComparer.OrdinalIgnoreCase);

    public TemplatesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadDataAsync();
    }

    public async void ReloadData()
    {
        await ReloadDataAsync();
    }

    private async Task ReloadDataAsync()
    {
        SetLoadingState(true);
        StatusInfoBar.IsOpen = false;

        try
        {
            _settings = _settingsStore.Load();
            _sortCriteria = string.IsNullOrWhiteSpace(_settings.TemplateSortCriteria)
                ? "CreatedAt"
                : _settings.TemplateSortCriteria;
            _sortAscending = _settings.TemplateSortAscending;
            _selectedEditorFilters = new HashSet<string>(
                _settings.TemplateEditorFilters ?? [],
                StringComparer.OrdinalIgnoreCase);
            _selectedTagFilters.Clear();
            foreach (var tag in _settings.TemplateTagFilters ?? [])
            {
                _selectedTagFilters.Add(tag);
            }
            if (HideMissingEditorsItem is not null)
            {
                HideMissingEditorsItem.IsChecked = _settings.TemplateHideMissingEditors;
            }
            SyncSortFlyout();

            var customEditorPaths = _settings.CustomEditorPaths ?? [];
            var data = await Task.Run(() =>
            {
                var templates = _templateService.GetCustomTemplates();
                var projects = _projectService.GetRecentProjects();
                var editors = _editorLocator.GetInstalledEditors(customEditorPaths);
                return (templates, projects, editors);
            });

            _allTemplates = data.templates;
            _allProjects = data.projects;
            _installedEditors = data.editors;
            RebuildFilterMenus();
            FilterAndDisplayTemplates();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TemplatesPage.ReloadDataAsync failed: {ex}");
            TemplatesGridView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            SummaryTextBlock.Text = string.Empty;
            ShowStatus("Templates could not be loaded", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void FilterAndDisplayTemplates()
    {
        foreach (var template in _allTemplates)
        {
            if (string.IsNullOrWhiteSpace(template.EditorVersion))
            {
                template.IsEditorInstalled = _installedEditors.Count > 0;
            }
            else
            {
                template.IsEditorInstalled = _installedEditors.Keys.Any(ver =>
                    string.Equals(ver, template.EditorVersion, StringComparison.OrdinalIgnoreCase) ||
                    ver.StartsWith(template.EditorVersion, StringComparison.OrdinalIgnoreCase));
            }
        }

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        IEnumerable<CustomTemplateInfo> filtered = string.IsNullOrWhiteSpace(query)
            ? _allTemplates
            : _allTemplates.Where(template =>
                template.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                template.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                template.EditorVersion.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                template.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (template.Tags != null && template.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))));

        if (HideMissingEditorsItem?.IsChecked == true)
        {
            filtered = filtered.Where(template => template.IsEditorInstalled);
        }

        if (_selectedEditorFilters.Count > 0)
        {
            filtered = filtered.Where(template =>
                _selectedEditorFilters.Any(version =>
                    IsTemplateForEditorVersion(template, version)));
        }

        if (_selectedTagFilters.Count > 0)
        {
            filtered = filtered.Where(template =>
                template.Tags != null &&
                template.Tags.Any(tag => _selectedTagFilters.Contains(tag)));
        }

        filtered = _sortCriteria switch
        {
            "Name" => _sortAscending
                ? filtered.OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
                : filtered.OrderByDescending(template => template.Name, StringComparer.CurrentCultureIgnoreCase),
            "Version" => _sortAscending
                ? filtered.OrderBy(template => template.Version, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(template => template.Version, StringComparer.OrdinalIgnoreCase),
            "EditorVersion" => _sortAscending
                ? filtered.OrderBy(template => template.EditorVersion, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(template => template.EditorVersion, StringComparer.OrdinalIgnoreCase),
            _ => _sortAscending
                ? filtered.OrderBy(template => template.CreatedAt)
                : filtered.OrderByDescending(template => template.CreatedAt)
        };

        var displayedTemplates = filtered.ToList();

        TemplatesGridView.ItemsSource = displayedTemplates;
        TemplatesGridView.Visibility = displayedTemplates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = displayedTemplates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (displayedTemplates.Count == 0 && _allTemplates.Count > 0)
        {
            EmptyStateTitle.Text = "No matching templates";
            EmptyStateMessage.Text = !string.IsNullOrWhiteSpace(query)
                ? $"No templates match “{query}”. Try a different search."
                : "No templates match the selected Editor versions.";
            EmptyStateActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyStateTitle.Text = "Create your first template";
            EmptyStateMessage.Text = "Save a configured Unity project as a reusable starting point.";
            EmptyStateActionButton.Visibility = Visibility.Visible;
        }

        SummaryTextBlock.Text = displayedTemplates.Count == _allTemplates.Count
            ? FormatTemplateCount(_allTemplates.Count)
            : $"{FormatTemplateCount(displayedTemplates.Count)} of {_allTemplates.Count}";
    }

    private static string FormatTemplateCount(int count) =>
        count == 1 ? "1 template" : $"{count} templates";

    private void SetLoadingState(bool isLoading)
    {
        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        TemplatesGridView.IsEnabled = !isLoading;
        SearchBox.IsEnabled = !isLoading;
        NewTemplateButton.IsEnabled = !isLoading;
        DisplayOptionsDropDownButton.IsEnabled = !isLoading;
        SortDropDownButton.IsEnabled = !isLoading;

        if (isLoading)
        {
            TemplatesGridView.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            FilterAndDisplayTemplates();
        }
    }

    private void RebuildFilterMenus()
    {
        RebuildEditorVersionFilterMenu();
        RebuildTagFilterMenu();
        UpdateFilterLabels();
    }

    private void RebuildEditorVersionFilterMenu()
    {
        if (EditorFilterSubItem is null) return;
        EditorFilterSubItem.Items.Clear();

        var clearItem = new MenuFlyoutItem
        {
            Text = "Clear filter",
            IsEnabled = _selectedEditorFilters.Count > 0
        };
        clearItem.Click += OnClearEditorFiltersClick;
        EditorFilterSubItem.Items.Add(clearItem);
        EditorFilterSubItem.Items.Add(new MenuFlyoutSeparator());

        var versions = _allTemplates
            .Select(template => template.EditorVersion?.Trim())
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (versions.Count == 0)
        {
            EditorFilterSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No Editor versions available",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var version in versions)
            {
                bool isInstalled = _installedEditors.Keys.Any(installedVersion =>
                    EditorVersionsMatch(version, installedVersion));

                var item = new ToggleMenuFlyoutItem
                {
                    Text = !isInstalled ? $"{FormatEditorVersion(version)} (Missing)" : FormatEditorVersion(version),
                    Tag = version,
                    IsChecked = _selectedEditorFilters.Contains(version)
                };
                if (!isInstalled && Resources.ContainsKey("MissingEditorVersionMenuItemStyle"))
                {
                    item.Style = (Style)Resources["MissingEditorVersionMenuItemStyle"];
                }
                item.Click += OnEditorVersionFilterClick;
                EditorFilterSubItem.Items.Add(item);
            }
        }
    }

    private void RebuildTagFilterMenu()
    {
        if (TagFilterSubItem is null) return;
        TagFilterSubItem.Items.Clear();

        var clearItem = new MenuFlyoutItem
        {
            Text = "Clear filter",
            IsEnabled = _selectedTagFilters.Count > 0
        };
        clearItem.Click += OnClearTagFiltersClick;
        TagFilterSubItem.Items.Add(clearItem);
        TagFilterSubItem.Items.Add(new MenuFlyoutSeparator());

        var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include standard Unity presets
        foreach (var tag in new[] { "Game", "Client Project", "Prototype", "Personal", "Simulation", "Archived", "Visualization", "Work in Progress", "2D", "3D" })
        {
            allTags.Add(tag);
        }

        // Include tags from loaded templates
        foreach (var template in _allTemplates)
        {
            if (template.Tags is not null)
            {
                foreach (var tag in template.Tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                        allTags.Add(tag.Trim());
                }
            }
        }

        // Include tags stored in settings
        if (_settings.ProjectTags is not null)
        {
            foreach (var tagsList in _settings.ProjectTags.Values)
            {
                foreach (var tag in tagsList)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                        allTags.Add(tag.Trim());
                }
            }
        }

        if (_selectedTagFilters.RemoveWhere(tag => !allTags.Contains(tag)) > 0)
        {
            SaveTemplateOptions();
        }

        var sortedTags = allTags
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (sortedTags.Count == 0)
        {
            TagFilterSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No tags available",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var tag in sortedTags)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = tag,
                    Tag = tag,
                    IsChecked = _selectedTagFilters.Contains(tag)
                };
                item.Click += OnTagFilterClick;
                TagFilterSubItem.Items.Add(item);
            }
        }
    }

    private void UpdateFilterLabels()
    {
        if (EditorFilterSubItem is not null)
        {
            EditorFilterSubItem.Text = _selectedEditorFilters.Count == 0
                ? "Filter by Editor version"
                : $"Editor version ({_selectedEditorFilters.Count})";
        }

        if (TagFilterSubItem is not null)
        {
            TagFilterSubItem.Text = _selectedTagFilters.Count == 0
                ? "Filter by tag"
                : $"Tag ({_selectedTagFilters.Count})";
        }
    }

    private void OnEditorVersionFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string version } item)
            return;

        if (item.IsChecked)
            _selectedEditorFilters.Add(version);
        else
            _selectedEditorFilters.Remove(version);

        SaveTemplateOptions();
        RebuildFilterMenus();
        FilterAndDisplayTemplates();
    }

    private void OnClearEditorFiltersClick(object sender, RoutedEventArgs e)
    {
        _selectedEditorFilters.Clear();
        SaveTemplateOptions();
        RebuildFilterMenus();
        FilterAndDisplayTemplates();
    }

    private void OnTagFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string tag } item)
            return;

        if (item.IsChecked)
            _selectedTagFilters.Add(tag);
        else
            _selectedTagFilters.Remove(tag);

        SaveTemplateOptions();
        RebuildFilterMenus();
        FilterAndDisplayTemplates();
    }

    private void OnClearTagFiltersClick(object sender, RoutedEventArgs e)
    {
        _selectedTagFilters.Clear();
        SaveTemplateOptions();
        RebuildFilterMenus();
        FilterAndDisplayTemplates();
    }

    private void OnDisplayOptionClick(object sender, RoutedEventArgs e)
    {
        SaveTemplateOptions();
        FilterAndDisplayTemplates();
    }

    private void OnSortOptionClick(object sender, RoutedEventArgs e)
    {
        _sortCriteria = SortTemplatesByNameItem?.IsChecked == true
            ? "Name"
            : SortTemplatesByVersionItem?.IsChecked == true
                ? "Version"
                : SortTemplatesByEditorVersionItem?.IsChecked == true
                    ? "EditorVersion"
                    : "CreatedAt";
        _sortAscending = SortTemplatesAscendingItem?.IsChecked == true;

        SaveTemplateOptions();
        FilterAndDisplayTemplates();
    }

    private void SaveTemplateOptions()
    {
        _settings.TemplateSortCriteria = _sortCriteria;
        _settings.TemplateSortAscending = _sortAscending;
        _settings.TemplateEditorFilters = _selectedEditorFilters
            .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.TemplateTagFilters = _selectedTagFilters
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.TemplateHideMissingEditors = HideMissingEditorsItem?.IsChecked ?? false;
        _settingsStore.Save(_settings);
        UpdateFilterLabels();
    }

    private void SyncSortFlyout()
    {
        if (SortTemplatesByNameItem is not null) SortTemplatesByNameItem.IsChecked = _sortCriteria == "Name";
        if (SortTemplatesByCreatedAtItem is not null) SortTemplatesByCreatedAtItem.IsChecked = _sortCriteria == "CreatedAt";
        if (SortTemplatesByVersionItem is not null) SortTemplatesByVersionItem.IsChecked = _sortCriteria == "Version";
        if (SortTemplatesByEditorVersionItem is not null) SortTemplatesByEditorVersionItem.IsChecked = _sortCriteria == "EditorVersion";
        if (SortTemplatesAscendingItem is not null) SortTemplatesAscendingItem.IsChecked = _sortAscending;
        if (SortTemplatesDescendingItem is not null) SortTemplatesDescendingItem.IsChecked = !_sortAscending;
    }

    private static bool IsTemplateForEditorVersion(
        CustomTemplateInfo template,
        string editorVersion)
    {
        if (string.IsNullOrWhiteSpace(template.EditorVersion))
        {
            return false;
        }

        return EditorVersionsMatch(template.EditorVersion, editorVersion);
    }

    private static bool EditorVersionsMatch(string firstVersion, string secondVersion)
    {
        return string.Equals(
                   firstVersion,
                   secondVersion,
                   StringComparison.OrdinalIgnoreCase) ||
               firstVersion.StartsWith(
                   secondVersion,
                   StringComparison.OrdinalIgnoreCase) ||
               secondVersion.StartsWith(
                   firstVersion,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEditorVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var major) &&
            int.TryParse(parts[1], out var minor))
        {
            var displayVersion = major >= 6000
                ? minor == 0 ? $"{major / 1000}" : $"{major / 1000}.{minor}"
                : $"{major}.{minor}";
            return $"Unity {displayVersion} ({version})";
        }

        return $"Unity {version}";
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await ReloadDataAsync();
    }

    private async void OnNewTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null)
            {
                return;
            }

            var dialog = new SaveProjectAsTemplateDialog(_allProjects, _installedEditors)
            {
                XamlRoot = activeXamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && dialog.ResultTemplate is not null)
            {
                await ReloadDataAsync();
                ShowStatus("Template created", $"“{dialog.ResultTemplate.Name}” is ready to use.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnNewTemplateClick failed: {ex}");
            ShowStatus("Template could not be created", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnCreateProjectFromTemplateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null)
            {
                return;
            }

            var dialog = new NewProjectDialog(_installedEditors.Keys, template)
            {
                XamlRoot = activeXamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(dialog.CreatedProjectPath))
            {
                _projectService.AddOrUpdateProject(dialog.CreatedProjectPath, dialog.CreatedProjectTitle, dialog.SelectedVersion);
                Frame.Navigate(typeof(ProjectsPage));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnCreateProjectFromTemplateClick failed: {ex}");
            ShowStatus("Project could not be created", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetLoadingState(false);
            FilterAndDisplayTemplates();
        }
    }

    private async void OnEditTemplateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null)
            {
                return;
            }

            var dialog = new SaveProjectAsTemplateDialog(template)
            {
                XamlRoot = activeXamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && dialog.ResultTemplate is not null)
            {
                await ReloadDataAsync();
                ShowStatus("Template updated", $"“{dialog.ResultTemplate.Name}” was updated.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnEditTemplateClick failed: {ex}");
            ShowStatus("Template could not be updated", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnManageTemplateTagsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null) return;

            var existingGlobalTags = _allTemplates.SelectMany(t => t.Tags ?? Enumerable.Empty<string>()).Distinct();
            var dialog = new Dialogs.ManageProjectTagsDialog(
                template.Name,
                template.Tags ?? [],
                "Template",
                existingGlobalTags)
            {
                XamlRoot = activeXamlRoot,
                RequestedTheme = (activeXamlRoot.Content as FrameworkElement)?.RequestedTheme ?? MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var updatedTags = dialog.ResultTags;
                var updatedTemplate = await _templateService.UpdateCustomTemplateAsync(
                    template,
                    template.Description ?? string.Empty,
                    template.Version,
                    replacementImagePath: null,
                    removeImage: false,
                    tags: updatedTags,
                    rewriteArchive: false);

                if (updatedTemplate is null)
                {
                    ShowStatus(
                        "Failed to update tags",
                        "The template metadata or archive could not be updated.",
                        InfoBarSeverity.Error);
                    return;
                }

                await ReloadDataAsync();
                ShowStatus("Tags updated", $"Tags for “{template.Name}” have been updated.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnManageTemplateTagsClick failed: {ex}");
            ShowStatus("Failed to update tags", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnShowInExplorerClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(template.TemplateFolderPath))
            {
                ShowStatus("Template folder not found", template.TemplateFolderPath, InfoBarSeverity.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = template.TemplateFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnShowInExplorerClick failed: {ex}");
            ShowStatus("Folder could not be opened", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnTemplatePropertiesClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null)
            {
                return;
            }

            var dialog = new TemplatePropertiesDialog(template)
            {
                XamlRoot = activeXamlRoot,
                RequestedTheme = MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnTemplatePropertiesClick failed: {ex}");
            ShowStatus("Properties could not be opened", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnDeleteTemplateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            var activeXamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (activeXamlRoot is null)
            {
                return;
            }

            var confirmDialog = new ContentDialog
            {
                Title = "Delete template?",
                Content = $"“{template.Name}” and its saved template files will be permanently deleted.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = activeXamlRoot,
                RequestedTheme = MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!_templateService.DeleteCustomTemplate(template.Id))
            {
                ShowStatus("Template could not be deleted", "The template files may be in use.", InfoBarSeverity.Error);
                return;
            }

            await ReloadDataAsync();
            ShowStatus("Template deleted", $"“{template.Name}” was removed.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnDeleteTemplateClick failed: {ex}");
            ShowStatus("Template could not be deleted", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnInstallMissingEditorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CustomTemplateInfo template)
        {
            return;
        }

        try
        {
            var xamlRoot = XamlRoot ?? Content?.XamlRoot;
            if (xamlRoot is null)
            {
                ShowStatus("Unable to open the Editor installer", "Window is not ready.", InfoBarSeverity.Error);
                return;
            }

            var targetTheme = (xamlRoot.Content as FrameworkElement)?.RequestedTheme
                ?? MainWindow.Instance?.CurrentTheme
                ?? ElementTheme.Default;

            UnityEditorRelease? release = null;
            if (!string.IsNullOrWhiteSpace(template.EditorVersion))
            {
                try
                {
                    release = await _releaseService.GetReleaseAsync(template.EditorVersion);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unable to resolve Unity {template.EditorVersion}: {ex}");
                }
            }

            if (release is null)
            {
                var dialog = new InstallEditorDialog(_installedEditors.Keys, template.EditorVersion)
                {
                    XamlRoot = xamlRoot,
                    RequestedTheme = targetTheme
                };

                await dialog.ShowAsync();
                release = dialog.SelectedRelease;
            }

            if (release is null)
            {
                return;
            }

            var installRoot = new UnityHubLocationSettingsService().GetInstallLocation();
            var modulesDialog = new AddModulesDialog(release, installRoot)
            {
                XamlRoot = xamlRoot,
                RequestedTheme = targetTheme
            };

            await modulesDialog.ShowAsync();
            if (modulesDialog.InstallationRequest is null)
            {
                return;
            }

            var result = UnityModuleInstallationManager.Instance.Enqueue(modulesDialog.InstallationRequest);
            ShowStatus(
                result.Accepted ? "Installation started" : "Installation warning",
                result.Message,
                result.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OnInstallMissingEditorClick failed: {ex}");
            ShowStatus("Unable to open the Editor installer", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }
}
