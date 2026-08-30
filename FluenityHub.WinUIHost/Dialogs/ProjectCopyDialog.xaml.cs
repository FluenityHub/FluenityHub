using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public enum ProjectCopyMode
{
    Backup,
    Clone,
    Restore
}

public sealed record ProjectCopyRequest(
    string TargetPath,
    bool IncludeUserSettings,
    bool IncludeGitHistory);

public sealed partial class ProjectCopyDialog : ContentDialog
{
    private readonly string _sourcePath;
    private readonly ProjectCopyMode _mode;
    private readonly Func<ProjectCopyRequest, IProgress<ProjectCopyProgress>, CancellationToken, Task> _operation;
    private readonly string _actionText;
    private CancellationTokenSource? _operationCancellation;
    private bool _isOperating;
    private bool _isResultState;

    public ProjectCopyDialog(
        UnityProjectInfo project,
        ProjectCopyMode mode,
        Func<ProjectCopyRequest, IProgress<ProjectCopyProgress>, CancellationToken, Task> operation,
        ProjectBackupRecord? backup = null)
    {
        InitializeComponent();
        _mode = mode;
        _operation = operation;
        _sourcePath = mode == ProjectCopyMode.Restore
            ? backup?.BackupPath ?? throw new ArgumentNullException(nameof(backup))
            : project.Path;

        var sourceFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(project.Path));
        switch (mode)
        {
            case ProjectCopyMode.Backup:
                Title = $"Back up {project.Title}";
                _actionText = "Back up";
                DescriptionTextBlock.Text =
                    "Create a recoverable copy without Unity's generated cache folders. The original project will not be changed.";
                DestinationTextBox.Text = ProjectBackupService.DefaultBackupRoot;
                FolderNameTextBox.Text = $"{sourceFolderName} {DateTime.Now:yyyy-MM-dd HHmmss}";
                IncludeUserSettingsCheckBox.IsChecked = true;
                IncludeGitHistoryCheckBox.IsChecked = true;
                break;

            case ProjectCopyMode.Clone:
                Title = $"Clone {project.Title}";
                _actionText = "Clone";
                DescriptionTextBlock.Text =
                    "Create an independent working copy and add it to the project list when complete.";
                DestinationTextBox.Text = Path.GetDirectoryName(project.Path) ?? string.Empty;
                FolderNameTextBox.Text = $"{sourceFolderName} Copy";
                IncludeUserSettingsCheckBox.IsChecked = false;
                IncludeGitHistoryCheckBox.IsChecked = false;
                break;

            case ProjectCopyMode.Restore:
                Title = $"Restore {project.Title} as a new project";
                _actionText = "Restore";
                DescriptionTextBlock.Text =
                    "Restore this backup into a new folder. The original project and backup will remain unchanged.";
                DestinationTextBox.Text = Path.GetDirectoryName(project.Path) ?? string.Empty;
                FolderNameTextBox.Text = $"{sourceFolderName} Restored";
                IncludeUserSettingsCheckBox.IsChecked = backup!.IncludesUserSettings;
                IncludeUserSettingsCheckBox.IsEnabled = false;
                IncludeGitHistoryCheckBox.IsChecked = backup.IncludesGitHistory;
                IncludeGitHistoryCheckBox.IsEnabled = backup.IncludesGitHistory;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        PrimaryButtonText = _actionText;
        ExclusionsInfoBar.Message =
            $"FluenityHub excludes generated root folders: {string.Join(", ", ProjectBackupService.ExcludedDirectoryNames)}. Reparse points are skipped.";
        Validate(showMessage: false);
    }

    public string TargetPath => Path.Combine(
        DestinationTextBox.Text.Trim(),
        FolderNameTextBox.Text.Trim());

    public bool IncludeUserSettings => IncludeUserSettingsCheckBox.IsChecked == true;

    public bool IncludeGitHistory => IncludeGitHistoryCheckBox.IsChecked == true;

    public bool OperationCompleted { get; private set; }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
        => Validate(showMessage: false);

    private async void OnBrowseDestinationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MainWindow.Instance is null)
            {
                return;
            }

            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(MainWindow.Instance.AppWindow.Id)
            {
                Title = _mode == ProjectCopyMode.Backup
                    ? "Choose where to store the backup"
                    : "Choose a destination for the project copy",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                DestinationTextBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_isOperating)
        {
            return;
        }

        var error = Validate(showMessage: true);
        if (!string.IsNullOrEmpty(error))
        {
            ShowSetupState();
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            BeginOperationState();
            var request = new ProjectCopyRequest(TargetPath, IncludeUserSettings, IncludeGitHistory);
            var progress = new Progress<ProjectCopyProgress>(UpdateOperationProgress);
            await _operation(request, progress, _operationCancellation!.Token);

            OperationCompleted = true;
            _isOperating = false;
            DisposeOperationCancellation();
            args.Cancel = false;
        }
        catch (OperationCanceledException)
        {
            ShowOperationResult(
                "Operation canceled",
                "The incomplete copy was removed. You can retry or change the destination.",
                InfoBarSeverity.Warning,
                showPaused: true);
        }
        catch (Exception ex)
        {
            ShowOperationResult(
                "Couldn't complete the operation",
                ex.Message,
                InfoBarSeverity.Error,
                showPaused: false);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_isResultState)
        {
            return;
        }

        args.Cancel = true;
        ShowSetupState();
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_isOperating)
        {
            return;
        }

        args.Cancel = true;
        RequestCancellation();
    }

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_isOperating || OperationCompleted)
        {
            return;
        }

        args.Cancel = true;
        RequestCancellation();
    }

    private void BeginOperationState()
    {
        _isOperating = true;
        _isResultState = false;
        OperationCompleted = false;
        _operationCancellation = new CancellationTokenSource();

        SetupPanel.Visibility = Visibility.Collapsed;
        OperationPanel.Visibility = Visibility.Visible;
        OperationDescriptionTextBlock.Text = "Calculating the files to copy...";
        OperationCurrentItemTextBlock.Text = "Waiting for file information...";
        OperationMetricsTextBlock.Text = string.Empty;
        OperationPercentageTextBlock.Text = string.Empty;
        OperationProgressBar.Value = 0;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.ShowError = false;
        OperationProgressBar.ShowPaused = false;
        OperationInfoBar.IsOpen = false;

        PrimaryButtonText = "Working...";
        IsPrimaryButtonEnabled = false;
        SecondaryButtonText = string.Empty;
        IsSecondaryButtonEnabled = false;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
    }

    private void UpdateOperationProgress(ProjectCopyProgress progress)
    {
        OperationDescriptionTextBlock.Text = progress.Status;
        OperationCurrentItemTextBlock.Text = string.IsNullOrWhiteSpace(progress.CurrentItem)
            ? "Finalizing the destination..."
            : progress.CurrentItem;

        if (progress.Percentage is double percentage)
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = percentage;
            OperationPercentageTextBlock.Text = $"{percentage:0}%";
        }
        else
        {
            OperationProgressBar.IsIndeterminate = true;
            OperationPercentageTextBlock.Text = string.Empty;
        }

        var fileText = progress.TotalFiles > 0
            ? $"{progress.FilesCopied:N0} of {progress.TotalFiles:N0} files"
            : string.Empty;
        var byteText = progress.TotalBytes > 0
            ? $"{FormatBytes(progress.BytesCopied)} of {FormatBytes(progress.TotalBytes)}"
            : string.Empty;
        OperationMetricsTextBlock.Text = string.Join(
            ", ",
            new[] { fileText, byteText }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private void ShowOperationResult(
        string title,
        string message,
        InfoBarSeverity severity,
        bool showPaused)
    {
        _isOperating = false;
        _isResultState = true;
        DisposeOperationCancellation();

        OperationDescriptionTextBlock.Text = title;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.ShowPaused = showPaused;
        OperationProgressBar.ShowError = severity == InfoBarSeverity.Error;
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.Severity = severity;
        OperationInfoBar.IsOpen = true;

        PrimaryButtonText = "Retry";
        IsPrimaryButtonEnabled = true;
        SecondaryButtonText = "Back";
        IsSecondaryButtonEnabled = true;
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Primary;
    }

    private void ShowSetupState()
    {
        _isResultState = false;
        OperationPanel.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        OperationInfoBar.IsOpen = false;
        PrimaryButtonText = _actionText;
        SecondaryButtonText = string.Empty;
        IsSecondaryButtonEnabled = false;
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Validate(showMessage: false);
    }

    private void RequestCancellation()
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested)
        {
            return;
        }

        OperationDescriptionTextBlock.Text = "Canceling and removing the incomplete copy...";
        OperationProgressBar.ShowPaused = true;
        CloseButtonText = "Canceling...";
        _operationCancellation.Cancel();
    }

    private void DisposeOperationCancellation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private string Validate(bool showMessage)
    {
        if (DestinationTextBox is null || FolderNameTextBox is null)
        {
            return string.Empty;
        }

        var error = ProjectBackupService.GetValidationError(
            _sourcePath,
            DestinationTextBox.Text,
            FolderNameTextBox.Text);
        IsPrimaryButtonEnabled = string.IsNullOrEmpty(error) && !_isOperating;

        if (showMessage && !string.IsNullOrEmpty(error))
        {
            ShowValidation(error);
        }
        else
        {
            ValidationTextBlock.Visibility = Visibility.Collapsed;
        }

        return error;
    }

    private void ShowValidation(string message)
    {
        ValidationTextBlock.Text = message;
        ValidationTextBlock.Visibility = Visibility.Visible;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = Math.Clamp((int)Math.Log(bytes, 1024), 0, units.Length - 1);
        return $"{bytes / Math.Pow(1024, unit):0.##} {units[unit]}";
    }
}
