using System.Diagnostics;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public enum ProjectBackupDialogAction
{
    None,
    Restore,
    Delete
}

public sealed partial class ProjectBackupsDialog : ContentDialog
{
    private readonly string _projectPath;
    private readonly ProjectBackupService _backupService;
    private readonly CancellationTokenSource _loadCancellation = new();
    private bool _isLoading;

    public ProjectBackupsDialog(
        string projectTitle,
        string projectPath,
        ProjectBackupService backupService)
    {
        InitializeComponent();
        Title = $"Backups for {projectTitle}";
        _projectPath = projectPath;
        _backupService = backupService;
        Loaded += OnDialogLoaded;
    }

    public ProjectBackupDialogAction RequestedAction { get; private set; }

    public ProjectBackupRecord? SelectedBackup { get; private set; }

    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnDialogLoaded;
        await LoadBackupsAsync();
    }

    private async void OnRetryLoadClick(object sender, RoutedEventArgs e)
        => await LoadBackupsAsync();

    private async Task LoadBackupsAsync()
    {
        if (_isLoading || _loadCancellation.IsCancellationRequested)
        {
            return;
        }

        _isLoading = true;
        ShowState(loading: true);
        try
        {
            var backups = await _backupService.GetBackupsForProjectAsync(
                _projectPath,
                _loadCancellation.Token);
            BackupsListView.ItemsSource = backups;
            ShowState(hasItems: backups.Count > 0);
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
            // The dialog is closing.
        }
        catch (Exception ex)
        {
            LoadErrorInfoBar.Message = ex.Message;
            ShowState(hasError: true);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ShowState(
        bool loading = false,
        bool hasItems = false,
        bool hasError = false)
    {
        LoadingStatePanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        BackupsListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = !loading && !hasItems && !hasError
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorStatePanel.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenBackupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProjectBackupRecord backup }
            || !Directory.Exists(backup.BackupPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = backup.BackupPath,
            UseShellExecute = true
        });
    }

    private void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectBackupRecord backup })
        {
            SelectedBackup = backup;
            RequestedAction = ProjectBackupDialogAction.Restore;
            Hide();
        }
    }

    private void OnDeleteBackupClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProjectBackupRecord backup })
        {
            SelectedBackup = backup;
            RequestedAction = ProjectBackupDialogAction.Delete;
            Hide();
        }
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
    }
}
