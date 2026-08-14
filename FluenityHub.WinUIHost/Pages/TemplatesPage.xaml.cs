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

    private List<CustomTemplateInfo> _allTemplates = [];
    private List<UnityProjectInfo> _allProjects = [];
    private Dictionary<string, string> _installedEditors = [];
    private AppSettings _settings = new();
    private string _sortCriteria = "CreatedAt";
    private bool _sortAscending;
    private List<TemplateEditorFilterOption> _editorFilterOptions = [];
    private HashSet<string> _selectedEditorFilters = new(StringComparer.OrdinalIgnoreCase);
    private ToggleMenuFlyoutItem? _allEditorVersionsMenuItem;

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
            PopulateEditorFilterOptions();
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
                template.Version.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (_selectedEditorFilters.Count > 0)
        {
            filtered = filtered.Where(template =>
                _selectedEditorFilters.Any(version =>
                    IsTemplateForEditorVersion(template, version)));
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

    private void PopulateEditorFilterOptions()
    {
        var versions = _allTemplates
            .Select(template => template.EditorVersion?.Trim())
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _selectedEditorFilters.IntersectWith(versions);
        _editorFilterOptions = versions.Select(version => new TemplateEditorFilterOption
        {
            Version = version,
            DisplayName = FormatEditorVersion(version),
            IsInstalled = _installedEditors.Keys.Any(installedVersion =>
                EditorVersionsMatch(version, installedVersion)),
            IsSelected = _selectedEditorFilters.Contains(version)
        }).ToList();

        EditorVersionFilterFlyout.Items.Clear();
        if (_editorFilterOptions.Count == 0)
        {
            EditorVersionFilterFlyout.Items.Add(new MenuFlyoutItem
            {
                Text = "No template Editor versions found",
                IsEnabled = false
            });
        }
        else
        {
            _allEditorVersionsMenuItem = new ToggleMenuFlyoutItem
            {
                Text = "All Editor versions",
                IsChecked = _selectedEditorFilters.Count == 0
            };
            _allEditorVersionsMenuItem.Click += OnAllEditorVersionsFilterClick;
            EditorVersionFilterFlyout.Items.Add(_allEditorVersionsMenuItem);
            EditorVersionFilterFlyout.Items.Add(new MenuFlyoutSeparator());

            foreach (var option in _editorFilterOptions)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = option.IsMissing
                        ? $"{option.DisplayName} (Missing)"
                        : option.DisplayName,
                    IsChecked = option.IsSelected,
                    Tag = option
                };
                if (option.IsMissing)
                {
                    item.Style = (Style)Resources["MissingEditorVersionMenuItemStyle"];
                }
                item.Click += OnEditorVersionFilterClick;
                EditorVersionFilterFlyout.Items.Add(item);
            }
        }

        SaveTemplateOptions();
    }

    private void OnAllEditorVersionsFilterClick(object sender, RoutedEventArgs e)
    {
        _selectedEditorFilters.Clear();
        foreach (var option in _editorFilterOptions)
        {
            option.IsSelected = false;
        }

        foreach (var item in EditorVersionFilterFlyout.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = ReferenceEquals(item, _allEditorVersionsMenuItem);
        }

        SaveTemplateOptions();
        FilterAndDisplayTemplates();
    }

    private void OnEditorVersionFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem
            {
                Tag: TemplateEditorFilterOption option
            } item)
        {
            return;
        }

        option.IsSelected = item.IsChecked;
        if (item.IsChecked)
        {
            _selectedEditorFilters.Add(option.Version);
        }
        else
        {
            _selectedEditorFilters.Remove(option.Version);
        }

        if (_allEditorVersionsMenuItem is not null)
        {
            _allEditorVersionsMenuItem.IsChecked = _selectedEditorFilters.Count == 0;
        }

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
        _settingsStore.Save(_settings);
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

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }
}
