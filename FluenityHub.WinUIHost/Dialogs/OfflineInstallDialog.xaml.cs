using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class OfflineInstallDialog : ContentDialog
{
    private readonly OfflineInstallerService _offlineService = new();
    private readonly UnityHubLocationSettingsService _locationSettings = new();
    private OfflineInstallerInfo? _inspectedInstaller;
    private readonly List<string> _logEntries = new();

    public OfflineInstallDialog(UnityEditorInfo? targetEditor = null)
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
        DefaultButton = ContentDialogButton.Close;

        if (targetEditor is not null)
        {
            TargetLocationTextBox.Text = targetEditor.InstallDirectory;
        }
        else
        {
            TargetLocationTextBox.Text = _locationSettings.GetInstallLocation();
        }

        AppendLog("[INFO] Select a local installer package (.exe, .msi, .json) to begin.");
    }

    private async void OnBrowseInstallerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var window = MainWindow.Instance;
            if (window is null) return;
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".msi");
            picker.FileTypeFilter.Add(".json");
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            InstallerPathTextBox.Text = file.Path;
            AppendLog($"[INFO] Selected installer package: {file.Path}");

            _inspectedInstaller = await _offlineService.InspectInstallerAsync(file.Path);
            if (_inspectedInstaller.IsValidInstaller)
            {
                AppendLog($"[OK] Detected {_inspectedInstaller.ModuleType} installer for version {_inspectedInstaller.DetectedVersion}");
                
                // If target location is default root and installer version is known, append version subfolder
                if (_inspectedInstaller.ModuleType == "Editor" && _inspectedInstaller.DetectedVersion != "Unknown")
                {
                    string rootPath = _locationSettings.GetInstallLocation();
                    TargetLocationTextBox.Text = Path.Combine(rootPath, _inspectedInstaller.DetectedVersion);
                }

                StatusInfoBar.IsOpen = true;
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Package Verified";
                StatusInfoBar.Message = $"Ready to install {_inspectedInstaller.ModuleType} ({_inspectedInstaller.DetectedVersion}).";
                IsPrimaryButtonEnabled = true;
                DefaultButton = ContentDialogButton.Primary;
            }
            else
            {
                AppendLog($"[WARN] Could not automatically identify Unity component from installer filename.");
                IsPrimaryButtonEnabled = true;
                DefaultButton = ContentDialogButton.Primary;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[FAIL] Unable to inspect file: {ex.Message}");
        }
    }

    private async void OnBrowseTargetLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            var window = MainWindow.Instance;
            if (window is null) return;
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            TargetLocationTextBox.Text = folder.Path;
            AppendLog($"[INFO] Target location set to: {folder.Path}");
        }
        catch (Exception ex)
        {
            AppendLog($"[FAIL] Unable to select target folder: {ex.Message}");
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            string installerPath = InstallerPathTextBox.Text;
            string targetPath = TargetLocationTextBox.Text;

            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                StatusInfoBar.IsOpen = true;
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Invalid Package";
                StatusInfoBar.Message = "Please select a valid offline installer file.";
                return;
            }

            IsPrimaryButtonEnabled = false;
            StatusInfoBar.IsOpen = true;
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "Installing / Repairing...";
            StatusInfoBar.Message = "Running offline setup process. Please respond to Windows UAC prompt if requested.";

            bool success = await _offlineService.InstallOrRepairFromOfflinePackageAsync(
                installerPath,
                targetPath,
                logMessage => DispatcherQueue.TryEnqueue(() => AppendLog(logMessage))
            );

            if (success)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Installation / Repair Completed";
                StatusInfoBar.Message = "Offline package installed and Editor structure updated successfully.";
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Error;
                StatusInfoBar.Title = "Installation / Repair Failed";
                StatusInfoBar.Message = "Offline setup finished with errors. Check log output for details.";
                IsPrimaryButtonEnabled = true;
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Process Error";
            StatusInfoBar.Message = ex.Message;
            IsPrimaryButtonEnabled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void AppendLog(string message)
    {
        _logEntries.Add(message);
        var paragraph = new Paragraph();

        for (int i = 0; i < _logEntries.Count; i++)
        {
            var log = _logEntries[i];
            var run = new Run { Text = i < _logEntries.Count - 1 ? log + "\n" : log };

            if (log.StartsWith("[FAIL]", StringComparison.OrdinalIgnoreCase))
            {
                run.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
            else if (log.StartsWith("[WARN]", StringComparison.OrdinalIgnoreCase))
            {
                run.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }
            else if (log.StartsWith("[OK]", StringComparison.OrdinalIgnoreCase))
            {
                run.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            }
            else
            {
                run.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }

            paragraph.Inlines.Add(run);
        }

        LogsRichTextBlock.Blocks.Clear();
        LogsRichTextBlock.Blocks.Add(paragraph);
    }

    private async void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_logEntries.Count > 0)
            {
                var dp = new DataPackage();
                dp.SetText(string.Join(Environment.NewLine, _logEntries));
                Clipboard.SetContent(dp);

                // Copy animation feedback
                if (CopyIconScaleTransform is not null)
                {
                    CopyButtonPulseAnimation.Begin();
                }

                CopyLogFontIcon.Glyph = "\uE73E";
                CopyLogTextBlock.Text = "Copied!";
                ToolTipService.SetToolTip(CopyLogButton, "Copied to clipboard!");

                await Task.Delay(2000);

                CopyLogFontIcon.Glyph = "\uE8C8";
                CopyLogTextBlock.Text = "Copy log";
                ToolTipService.SetToolTip(CopyLogButton, "Copy log to clipboard");
            }
        }
        catch { }
    }
}
