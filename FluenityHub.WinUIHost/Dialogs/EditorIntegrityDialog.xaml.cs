using System;
using System.Collections.Generic;
using System.Linq;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class EditorIntegrityDialog : ContentDialog
{
    private readonly UnityEditorInfo _editor;
    private readonly UnityEditorIntegrityService _integrityService = new();
    private EditorIntegrityReport? _lastReport;

    public EditorIntegrityDialog(UnityEditorInfo editor)
    {
        InitializeComponent();
        _editor = editor;
        EditorVersionTitleTextBlock.Text = editor.DisplayName;
        EditorPathTextBlock.Text = editor.InstallDirectory;
        RunDiagnosticCheck();
    }

    private async void RunDiagnosticCheck()
    {
        try
        {
            IsPrimaryButtonEnabled = false;
            StatusInfoBar.Severity = InfoBarSeverity.Informational;
            StatusInfoBar.Title = "Scanning installation...";
            StatusInfoBar.Message = "Verifying Unity binaries, Mono runtime, and module structures.";

            _lastReport = await _integrityService.CheckIntegrityAsync(_editor);
            PopulateRichTextLogs(_lastReport.DiagnosticLogs);

            if (_lastReport.IsHealthy)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Success;
                StatusInfoBar.Title = "Installation Healthy";
                StatusInfoBar.Message = "All core binaries, Mono runtime, and playback engine modules are verified.";
                IsPrimaryButtonEnabled = false;
                DefaultButton = ContentDialogButton.Close;
            }
            else
            {
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "Integrity Issues Detected";
                StatusInfoBar.Message = $"{_lastReport.MissingOrCorruptedItems.Count} issue(s) found. Click Repair Installation to resolve.";
                IsPrimaryButtonEnabled = true;
                DefaultButton = ContentDialogButton.Primary;
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Diagnostic Failed";
            StatusInfoBar.Message = ex.Message;
        }
    }

    private void PopulateRichTextLogs(List<string> logs)
    {
        DiagnosticLogsRichTextBlock.Blocks.Clear();
        var paragraph = new Paragraph();

        for (int i = 0; i < logs.Count; i++)
        {
            var log = logs[i];
            var run = new Run { Text = i < logs.Count - 1 ? log + "\n" : log };

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

        DiagnosticLogsRichTextBlock.Blocks.Add(paragraph);
    }

    private async void OnCopyLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastReport is not null && _lastReport.DiagnosticLogs.Count > 0)
            {
                var dp = new DataPackage();
                dp.SetText(string.Join(Environment.NewLine, _lastReport.DiagnosticLogs));
                Clipboard.SetContent(dp);

                // Give the copy action a short visual confirmation.
                CopyButtonPulseAnimation.Begin();

                CopyLogFontIcon.Glyph = "\uE73E"; // Checkmark icon
                CopyLogTextBlock.Text = "Copied!";
                ToolTipService.SetToolTip(CopyLogButton, "Copied to clipboard!");

                await Task.Delay(2000);

                CopyLogFontIcon.Glyph = "\uE8C8"; // Copy icon
                CopyLogTextBlock.Text = "Copy log";
                ToolTipService.SetToolTip(CopyLogButton, "Copy diagnostic log to clipboard");
            }
        }
        catch
        {
            // A clipboard failure should not hide the diagnostic result.
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (_lastReport is not null)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Informational;
                StatusInfoBar.Title = "Repairing Installation...";
                StatusInfoBar.Message = "Clearing temporary locks and repairing module registries.";

                bool success = await _integrityService.RepairInstallationAsync(_editor, _lastReport);
                if (success)
                {
                    // Refresh the report so the repaired state is visible.
                    _lastReport = await _integrityService.CheckIntegrityAsync(_editor);
                    PopulateRichTextLogs(_lastReport.DiagnosticLogs);

                    if (_lastReport.IsHealthy)
                    {
                        StatusInfoBar.Severity = InfoBarSeverity.Success;
                        StatusInfoBar.Title = "Repair Successful & Healthy";
                        StatusInfoBar.Message = "All core binaries, Mono runtime, and playback engine modules are verified.";
                        IsPrimaryButtonEnabled = false;
                        DefaultButton = ContentDialogButton.Close;
                    }
                    else
                    {
                        StatusInfoBar.Severity = InfoBarSeverity.Error;
                        StatusInfoBar.Title = "Core Binaries Missing from Disk";
                        StatusInfoBar.Message = $"{_lastReport.MissingOrCorruptedItems.Count} issue(s) remaining. Core files (Unity.exe/Mono) must be installed via Unity setup.";
                        IsPrimaryButtonEnabled = true;
                    }
                }
                else
                {
                    StatusInfoBar.Severity = InfoBarSeverity.Error;
                    StatusInfoBar.Title = "Repair Incomplete";
                    StatusInfoBar.Message = "Some files could not be repaired automatically.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "Repair Failed";
            StatusInfoBar.Message = ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }
}
