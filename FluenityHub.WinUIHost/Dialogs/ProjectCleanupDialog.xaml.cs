using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class ProjectCleanupDialog : ContentDialog
{
    private readonly string _projectPath;

    private long _librarySizeBytes;
    private long _tempSizeBytes;
    private long _logsSizeBytes;
    private long _buildsSizeBytes;
    private bool _isCleaning;

    public long TotalFreedBytes { get; private set; }
    public int DeletedFolderCount { get; private set; }
    public int FailedFolderCount { get; private set; }

    public ProjectCleanupDialog(string projectTitle, string projectPath)
    {
        InitializeComponent();
        _projectPath = projectPath;

        ProjectTitleTextBlock.Text = projectTitle;
        ProjectPathTextBlock.Text = projectPath;

        Loaded += OnDialogLoaded;
    }

    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ScanFolderSizesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"OnDialogLoaded error: {ex}");
        }
    }

    private async Task ScanFolderSizesAsync()
    {
        if (ScanProgressRing is not null)
        {
            ScanProgressRing.IsActive = true;
            ScanProgressRing.Visibility = Visibility.Visible;
        }

        try
        {
            await Task.Run(() =>
            {
                var libraryDir = Path.Combine(_projectPath, "Library");
                var tempDir = Path.Combine(_projectPath, "Temp");
                var objDir = Path.Combine(_projectPath, "Obj");
                var logsDir = Path.Combine(_projectPath, "Logs");
                var buildDir = Path.Combine(_projectPath, "Build");
                var buildsDir = Path.Combine(_projectPath, "Builds");

                _librarySizeBytes = GetDirectorySize(libraryDir);
                _tempSizeBytes = GetDirectorySize(tempDir) + GetDirectorySize(objDir);
                _logsSizeBytes = GetDirectorySize(logsDir);
                _buildsSizeBytes = GetDirectorySize(buildDir) + GetDirectorySize(buildsDir);
            });

            LibrarySizeTextBlock.Text = FormatBytes(_librarySizeBytes);
            TempSizeTextBlock.Text = FormatBytes(_tempSizeBytes);
            LogsSizeTextBlock.Text = FormatBytes(_logsSizeBytes);
            BuildsSizeTextBlock.Text = FormatBytes(_buildsSizeBytes);

            UpdateTotalPotential();
        }
        catch (Exception ex)
        {
            if (CleanupExpander is not null) CleanupExpander.Description = $"Scanning error: {ex.Message}";
        }
        finally
        {
            if (ScanProgressRing is not null)
            {
                ScanProgressRing.IsActive = false;
                ScanProgressRing.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnOptionCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (_isCleaning)
        {
            return;
        }

        UpdateTotalPotential();
    }

    private void UpdateTotalPotential()
    {
        long selectedTotal = 0;
        if (CleanLibraryCheckBox?.IsChecked == true) selectedTotal += _librarySizeBytes;
        if (CleanTempCheckBox?.IsChecked == true) selectedTotal += _tempSizeBytes;
        if (CleanLogsCheckBox?.IsChecked == true) selectedTotal += _logsSizeBytes;
        if (CleanBuildsCheckBox?.IsChecked == true) selectedTotal += _buildsSizeBytes;

        if (CleanupExpander is not null)
        {
            CleanupExpander.Description = $"{FormatBytes(selectedTotal)} can be freed from selected items";
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var selectedFolders = GetSelectedCleanupFolders().ToList();
            if (selectedFolders.Count == 0)
            {
                args.Cancel = true;
                ShowError("Select at least one cleanup option.");
                return;
            }

            BeginCleanupState(selectedFolders.Count);
            await Task.Yield();

            var result = await DeleteSelectedFoldersAsync(selectedFolders);
            TotalFreedBytes = result.FreedBytes;
            DeletedFolderCount = result.DeletedFolders;
            FailedFolderCount = result.Failures.Count;

            if (result.Failures.Count > 0)
            {
                args.Cancel = true;
                EndCleanupState();
                ShowError($"Some folders could not be deleted: {string.Join(", ", result.Failures.Take(3))}");
                return;
            }
        }
        catch (Exception ex)
        {
            args.Cancel = true;
            EndCleanupState();
            ShowError($"Failed to clean project: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            var dirInfo = new DirectoryInfo(path);
            return dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }

    private IEnumerable<string> GetSelectedCleanupFolders()
    {
        if (CleanLibraryCheckBox?.IsChecked == true) yield return Path.Combine(_projectPath, "Library");
        if (CleanTempCheckBox?.IsChecked == true)
        {
            yield return Path.Combine(_projectPath, "Temp");
            yield return Path.Combine(_projectPath, "Obj");
        }
        if (CleanLogsCheckBox?.IsChecked == true) yield return Path.Combine(_projectPath, "Logs");
        if (CleanBuildsCheckBox?.IsChecked == true)
        {
            yield return Path.Combine(_projectPath, "Build");
            yield return Path.Combine(_projectPath, "Builds");
        }
    }

    private async Task<CleanupResult> DeleteSelectedFoldersAsync(IReadOnlyList<string> selectedFolders)
    {
        long freed = 0;
        var deletedFolders = 0;
        var failures = new List<string>();

        for (int i = 0; i < selectedFolders.Count; i++)
        {
            var folder = selectedFolders[i];
            if (CleanupStatusTextBlock is not null)
            {
                CleanupStatusTextBlock.Text = $"Cleaning {Path.GetFileName(folder)} ({i + 1} of {selectedFolders.Count})...";
            }
            if (CleanupProgressBar is not null)
            {
                CleanupProgressBar.Value = i * 100d / selectedFolders.Count;
            }

            var result = await Task.Run(() => SafeDeleteDirectory(folder));
            freed += result.FreedBytes;
            if (result.Deleted)
            {
                deletedFolders++;
            }
            else if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                failures.Add($"{Path.GetFileName(folder)} ({result.ErrorMessage})");
            }
            if (CleanupProgressBar is not null)
            {
                CleanupProgressBar.Value = (i + 1) * 100d / selectedFolders.Count;
            }
        }

        return new CleanupResult(freed, deletedFolders, failures);
    }

    private void BeginCleanupState(int folderCount)
    {
        _isCleaning = true;
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        PrimaryButtonText = "Cleaning...";

        SetCleanupOptionCardsEnabled(false);

        if (StatusTextBlock is not null)
        {
            StatusTextBlock.Visibility = Visibility.Collapsed;
        }

        if (CleanupStatusTextBlock is not null)
        {
            CleanupStatusTextBlock.Text = $"Cleaning {folderCount} selected folder(s)...";
        }

        if (CleanupProgressBar is not null)
        {
            CleanupProgressBar.IsIndeterminate = false;
            CleanupProgressBar.ShowError = false;
            CleanupProgressBar.Value = 0;
        }

        if (CleanupProgressPanel is not null)
        {
            CleanupProgressPanel.Visibility = Visibility.Visible;
        }
    }

    private void EndCleanupState()
    {
        _isCleaning = false;
        IsPrimaryButtonEnabled = true;
        IsSecondaryButtonEnabled = true;
        PrimaryButtonText = "Clean";

        SetCleanupOptionCardsEnabled(true);

        if (CleanupProgressPanel is not null)
        {
            CleanupProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCleanupOptionCardsEnabled(bool isEnabled)
    {
        CleanLibraryCheckBox.IsEnabled = isEnabled;
        CleanTempCheckBox.IsEnabled = isEnabled;
        CleanLogsCheckBox.IsEnabled = isEnabled;
        CleanBuildsCheckBox.IsEnabled = isEnabled;
    }

    private (bool Deleted, long FreedBytes, string ErrorMessage) SafeDeleteDirectory(string path)
    {
        if (!IsCleanupFolderInsideProject(path))
        {
            return (false, 0, "invalid path");
        }

        if (!Directory.Exists(path)) return (false, 0, string.Empty);

        try
        {
            long size = GetDirectorySize(path);
            ClearReadOnlyAttributes(path);
            Directory.Delete(path, recursive: true);
            return (true, size, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private bool IsCleanupFolderInsideProject(string path)
    {
        try
        {
            var projectFullPath = Path.GetFullPath(_projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetFullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!targetFullPath.StartsWith(projectFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var folderName = Path.GetFileName(targetFullPath);
            return folderName is "Library" or "Temp" or "Obj" or "Logs" or "Build" or "Builds";
        }
        catch
        {
            return false;
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private void ShowError(string message)
    {
        if (StatusTextBlock is not null)
        {
            StatusTextBlock.Text = message;
            StatusTextBlock.Visibility = Visibility.Visible;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int digitGroup = (int)(Math.Log10(bytes) / Math.Log10(1024));
        digitGroup = Math.Clamp(digitGroup, 0, units.Length - 1);
        double number = bytes / Math.Pow(1024, digitGroup);
        return $"{number:0.##} {units[digitGroup]}";
    }

    private sealed record CleanupResult(long FreedBytes, int DeletedFolders, List<string> Failures);
}
