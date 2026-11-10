using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using FluenityHub_WinUIHost.Dialogs;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace FluenityHub_WinUIHost.Pages;

public sealed partial class NewProjectPage : Page
{
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private CancellationTokenSource? _editorLoad;
    private CancellationTokenSource? _cacheCheck;
    private List<UnityProjectTemplateItem> _templateItems = [];
    private bool _startedEditorLoad;
    private bool _updatingGallery;
    private bool _removingDownload;
    private bool _wideLayout;
    private int _visibleTemplateCount;
    private bool _revealGalleryWhenReady;
    internal bool IsCreating => ProjectForm.IsCreating;

    public NewProjectPage()
    {
        InitializeComponent();
        ProjectsBreadcrumbBar.ItemsSource = new[] { "Projects", "New project" };
        ProjectForm.StateChanged += OnFormStateChanged;
        ProjectForm.TemplatesChanged += OnTemplatesChanged;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_startedEditorLoad) return;
        _startedEditorLoad = true;
        using var request = new CancellationTokenSource();
        _editorLoad = request;
        var token = request.Token;
        try
        {
            var installedEditors = await Task.Run(() =>
                new UnityEditorLocator().GetInstalledEditors(new AppSettingsStore().Load().CustomEditorPaths), token);
            token.ThrowIfCancellationRequested();
            if (installedEditors.Count == 0)
            {
                ShowEditorLoadWarning("No Unity Editor found", "Install or locate a Unity Editor before creating a project.", showInstalls: true);
                return;
            }

            string? defaultVersion = null;
            try
            {
                defaultVersion = await Task.Run(() => UnityEditorPreferenceService.GetDefaultVersionAsync(token), token);
                EditorDiscoveryInfoBar.IsOpen = false;
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ShowEditorLoadWarning("Default Editor unavailable", "Choose an installed Editor to continue.", showInstalls: false);
            }

            token.ThrowIfCancellationRequested();
            ProjectForm.Configure(installedEditors, initialVersion: defaultVersion, showGallery: true);
            ProjectForm.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!token.IsCancellationRequested)
                ShowEditorLoadWarning("Could not find Unity Editors", "Open Installs to check your Editor locations.", showInstalls: true);
        }
        finally
        {
            _editorLoad = null;
            if (!token.IsCancellationRequested)
                UpdateGalleryLoadingState();
        }
    }

    private void ShowEditorLoadWarning(string title, string message, bool showInstalls)
    {
        EditorDiscoveryInfoBar.Title = title;
        EditorDiscoveryInfoBar.Message = message;
        EditorDiscoveryInfoBar.Severity = InfoBarSeverity.Warning;
        EditorDiscoveryInfoBar.IsClosable = !showInstalls;
        OpenInstallsButton.Visibility = showInstalls ? Visibility.Visible : Visibility.Collapsed;
        EditorDiscoveryInfoBar.IsOpen = true;
    }

    private void OnOpenInstallsClick(object sender, RoutedEventArgs args) => MainPage.Instance?.NavigateToEditors();

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (IsCreating) e.Cancel = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _editorLoad?.Cancel();
        _cacheCheck?.Cancel();
        foreach (var item in _templateItems) item.PropertyChanged -= OnTemplateItemChanged;
        GalleryRevealStoryboard.Stop();
        TemplatesGallery.ItemsSource = null;
        ProjectForm.Close();
        base.OnNavigatedFrom(e);
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 768;
        if (_wideLayout == wide) return;
        _wideLayout = wide;
        GalleryRow.Height = wide ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        FormRow.Height = new GridLength(wide ? 1 : 3, GridUnitType.Star);
        GalleryColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        FormColumn.Width = wide ? new GridLength(352) : new GridLength(1, GridUnitType.Star);
        Workspace.ColumnSpacing = wide ? 16 : 0;
        Workspace.RowSpacing = wide ? 0 : 16;
        Grid.SetRow(GalleryPanel, wide ? 1 : 0);
        Grid.SetColumn(GalleryPanel, wide ? 0 : 1);
    }

    private void OnTemplateSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ApplyTemplateFilter();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs args) => ApplyTemplateFilter();

    private async void OnRefreshTemplatesClick(object sender, RoutedEventArgs args)
        => await ProjectForm.RefreshTemplatesAsync();

    private void OnTemplatesChanged(object? sender, EventArgs args)
    {
        _cacheCheck?.Cancel();
        foreach (var item in _templateItems) item.PropertyChanged -= OnTemplateItemChanged;
        var downloadDirectory = new UnityHubLocationSettingsService().GetDownloadLocation();
        _templateItems = ProjectForm.TemplateOptions.Select(template =>
            UnityTemplateDownloadService.GetItem(template, downloadDirectory)).ToList();
        foreach (var item in _templateItems) item.PropertyChanged += OnTemplateItemChanged;
        ApplyTemplateFilter(animateAdditions: true);
        _ = CheckCachedArchivesAsync();
    }

    private async Task CheckCachedArchivesAsync()
    {
        using var request = new CancellationTokenSource();
        _cacheCheck = request;
        try
        {
            foreach (var item in _templateItems)
            {
                if (item.Template.PackageName.Length == 0 || item.IsDownloading) continue;
                try
                {
                    var archive = await Task.Run(() => UnityTemplateCatalogService.FindCachedArchiveAsync(
                        item.Template, item.DownloadDirectory, request.Token), request.Token);
                    if (!request.IsCancellationRequested && !item.IsDownloading) item.Update(archive is not null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Template cache check failed: {ex.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_cacheCheck, request)) _cacheCheck = null; }
    }

    private void OnTemplateItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UnityProjectTemplateItem.IsDownloaded) &&
            sender is UnityProjectTemplateItem { IsDownloaded: true } downloaded && XamlRoot is not null &&
            FocusManager.GetFocusedElement(XamlRoot) is Button button && ReferenceEquals(button.Tag, downloaded))
        {
            // Keep focus in the gallery when its Download button becomes unavailable.
            for (DependencyObject? parent = VisualTreeHelper.GetParent(button); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ItemContainer container) { container.Focus(FocusState.Programmatic); break; }
        }
        if (args.PropertyName is nameof(UnityProjectTemplateItem.IsDownloading) or nameof(UnityProjectTemplateItem.IsDownloaded))
            OnFormStateChanged(this, EventArgs.Empty);
        if (args.PropertyName == nameof(UnityProjectTemplateItem.ErrorMessage) && sender is UnityProjectTemplateItem { ErrorMessage.Length: > 0 } item)
        {
            DownloadErrorInfoBar.Title = "Template couldn't be downloaded";
            DownloadErrorInfoBar.Message = item.ErrorMessage;
            DownloadErrorInfoBar.IsOpen = true;
        }
    }

    private void OnDownloadTemplateClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not UnityProjectTemplateItem { CanUseDownloadCommand: true } item ||
            IsCreating || _removingDownload) return;
        DownloadErrorInfoBar.IsOpen = false;
        _ = UnityTemplateDownloadService.DownloadAsync(item);
    }

    private void OnOpenTemplateInExplorerClick(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: UnityProjectTemplateItem { LocalPath: { } path } }) return;
        try
        {
            var folder = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            if (folder is null || !Directory.Exists(folder))
                throw new DirectoryNotFoundException("The template location is no longer available.");
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            DownloadErrorInfoBar.Title = "Template location couldn't be opened";
            DownloadErrorInfoBar.Message = ex.Message;
            DownloadErrorInfoBar.IsOpen = true;
        }
    }

    private void OnCopyTemplatePathClick(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: UnityProjectTemplateItem { LocalPath: { } path } }) return;
        try
        {
            var data = new DataPackage();
            data.SetText($"\"{path}\"");
            Clipboard.SetContent(data);
        }
        catch (COMException ex)
        {
            DownloadErrorInfoBar.Title = "Template path couldn't be copied";
            DownloadErrorInfoBar.Message = ex.Message;
            DownloadErrorInfoBar.IsOpen = true;
        }
    }

    private async void OnTemplatePropertiesClick(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: UnityProjectTemplateItem item } || XamlRoot is null) return;
        var dialog = item.Template.CustomTemplate is { } custom
            ? new TemplatePropertiesDialog(custom)
            : new TemplatePropertiesDialog(item, ProjectForm.EditorVersion);
        dialog.XamlRoot = XamlRoot;
        if (MainWindow.Instance is { } window) dialog.RequestedTheme = window.CurrentTheme;
        await dialog.ShowAsync();
    }

    private async void OnRemoveTemplateDownloadClick(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: UnityProjectTemplateItem { CanRemoveDownload: true } item } ||
            IsCreating || _removingDownload || XamlRoot is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove template download?",
            Content = new TextBlock
            {
                Text = $"Remove the downloaded copy of {item.Template.Name}? You can download it again later. Existing projects won't be affected.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (MainWindow.Instance is { } window) dialog.RequestedTheme = window.CurrentTheme;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !item.CanRemoveDownload) return;

        _removingDownload = true;
        _cacheCheck?.Cancel();
        OnFormStateChanged(this, EventArgs.Empty);
        DownloadErrorInfoBar.IsOpen = false;
        try
        {
            await Task.Run(() => UnityTemplateCatalogService.RemoveCachedArchiveAsync(item.Template, item.DownloadDirectory));
            item.Update(downloaded: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DownloadErrorInfoBar.Title = "Download couldn't be removed";
            DownloadErrorInfoBar.Message = ex.Message;
            DownloadErrorInfoBar.IsOpen = true;
        }
        finally
        {
            _removingDownload = false;
            if (IsLoaded)
            {
                OnFormStateChanged(this, EventArgs.Empty);
                _ = CheckCachedArchivesAsync();
            }
        }
    }

    private void ApplyTemplateFilter(bool animateAdditions = false)
    {
        if (ProjectForm is null || TemplatesGallery is null || NoTemplatesTextBlock is null) return;
        var query = TemplateSearchBox.Text.Trim();
        var category = CategoryComboBox.SelectedItem as string;
        var choices = _templateItems.Where(item =>
            (category is null or "All" || item.Template.Category == category) &&
            (query.Length == 0 || item.Template.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.Template.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var selected = ProjectForm.SelectedTemplateOption;
        var selectedItem = choices.FirstOrDefault(item => item.Template == selected);
        if (selectedItem is null) selected = null;
        _updatingGallery = true;
        try
        {
            TemplatesGallery.ItemsSource = choices;
            if (selectedItem is not null) TemplatesGallery.Select(choices.IndexOf(selectedItem));
            NoTemplatesTextBlock.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingGallery = false; }
        if (animateAdditions && choices.Count > Math.Max(1, _visibleTemplateCount) && IsLoaded && _uiSettings.AnimationsEnabled)
            _revealGalleryWhenReady = true;
        _visibleTemplateCount = choices.Count;
        UpdateGalleryLoadingState();
        ProjectForm.SelectTemplateOption(selected);
    }

    private void OnGallerySelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (!_updatingGallery) ProjectForm.SelectTemplateOption((TemplatesGallery.SelectedItem as UnityProjectTemplateItem)?.Template);
    }

    private void OnFormStateChanged(object? sender, EventArgs args)
    {
        UpdateGalleryLoadingState();
        CreateButton.IsEnabled = ProjectForm.CanCreate && !_removingDownload &&
            !_templateItems.Any(item => item.Template == ProjectForm.SelectedTemplateOption && item.IsDownloading);
        CancelButton.IsEnabled = !IsCreating || ProjectForm.CanCancelCreation;
        CancelButton.Content = ProjectForm.IsCanceling ? "Canceling..." : "Cancel";
        TemplateSearchBox.IsEnabled = !IsCreating;
        CategoryComboBox.IsEnabled = !IsCreating;
        TemplatesGallery.IsEnabled = !IsCreating && !_removingDownload;
    }

    private void UpdateGalleryLoadingState()
    {
        if (LoadingPanel is null || LoadingRing is null || TemplatesGallery is null) return;
        var loading = _editorLoad is not null || !_startedEditorLoad || ProjectForm.IsLoadingTemplates;
        LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;
        RefreshTemplatesButton.IsEnabled = !loading && !IsCreating && !_removingDownload && ProjectForm.Visibility == Visibility.Visible;
        TemplatesGallery.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        NoTemplatesTextBlock.Visibility = !loading && _visibleTemplateCount == 0 &&
            ProjectForm.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (!loading && _revealGalleryWhenReady)
        {
            _revealGalleryWhenReady = false;
            if (_uiSettings.AnimationsEnabled) GalleryRevealStoryboard.Begin();
        }
    }

    private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0) GoBack();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (IsCreating) ProjectForm.CancelCreation();
        else GoBack();
    }

    private void GoBack()
    {
        if (!IsCreating && Frame?.CanGoBack == true) Frame.GoBack();
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (!await ProjectForm.CreateAsync())
        {
            _ = CheckCachedArchivesAsync();
            return;
        }
        var title = ProjectForm.CreatedProjectTitle;
        try
        {
            new UnityHubProjectService().AddOrUpdateProject(
                ProjectForm.CreatedProjectPath, title, ProjectForm.SelectedVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ProjectForm.ShowError($"Project files were created, but the project couldn't be added to the list: {ex.Message}");
            return;
        }
        GoBack();
        MainPage.Instance?.CurrentProjectsPage?.NotifyProjectCreated(title);
    }
}
