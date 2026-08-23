using System.Diagnostics;
using System.Text;
using FluenityHub_WinUIHost.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityCliLaunchService
{
    private static readonly UnityCliToolService CliToolService = new();

    public static async Task<bool> LaunchTerminalAsync(
        string? workingDirectory,
        string? editorExecutablePath,
        XamlRoot? xamlRoot,
        ElementTheme theme = ElementTheme.Default)
    {
        var status = CliToolService.GetStatus();
        if (!status.IsInstalled || string.IsNullOrWhiteSpace(status.ExecutablePath) || !File.Exists(status.ExecutablePath))
        {
            if (xamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    Title = "Unity CLI not installed",
                    Content = "Unity's standalone CLI tool is required to launch command-line sessions. Would you like to open Settings to install it?",
                    PrimaryButtonText = "Open Settings",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot,
                    RequestedTheme = theme,
                    Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    MainPage.Instance?.NavigateToSettings();
                }
            }
            return false;
        }

        var cliDirectory = Path.GetDirectoryName(status.ExecutablePath) ?? string.Empty;
        var targetDir = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var editorDir = !string.IsNullOrWhiteSpace(editorExecutablePath) && File.Exists(editorExecutablePath)
            ? Path.GetDirectoryName(editorExecutablePath)
            : null;

        return LaunchTerminalProcess(targetDir, cliDirectory, editorDir, status.Version, status.ExecutablePath);
    }

    private static bool LaunchTerminalProcess(
        string workingDirectory,
        string cliDirectory,
        string? editorDirectory,
        string? cliVersion,
        string? cliPath)
    {
        var sb = new StringBuilder();
        var escapedCliDir = cliDirectory.Replace("'", "''");
        var escapedWorkDir = workingDirectory.Replace("'", "''");
        var escapedCliVer = (cliVersion ?? "1.0.0").Replace("'", "''");
        var escapedCliPath = (cliPath ?? string.Empty).Replace("'", "''");

        sb.AppendLine($"$cliDir = '{escapedCliDir}';");
        sb.AppendLine("if (Test-Path $cliDir) { $env:Path = \"$cliDir;$env:Path\" }");

        if (!string.IsNullOrWhiteSpace(editorDirectory))
        {
            var escapedEditorDir = editorDirectory.Replace("'", "''");
            sb.AppendLine($"$editorDir = '{escapedEditorDir}';");
            sb.AppendLine("if (Test-Path $editorDir) { $env:Path = \"$editorDir;$env:Path\" }");
        }

        sb.AppendLine("Clear-Host;");
        sb.AppendLine("Write-Host '==================================================' -ForegroundColor Cyan;");
        sb.AppendLine("Write-Host '               Unity CLI Environment              ' -ForegroundColor Cyan;");
        sb.AppendLine("Write-Host '==================================================' -ForegroundColor Cyan;");
        sb.AppendLine($"Write-Host 'Directory : {escapedWorkDir}' -ForegroundColor Gray;");
        sb.AppendLine($"Write-Host 'Unity CLI : {escapedCliVer} ({escapedCliPath})' -ForegroundColor Gray;");

        if (!string.IsNullOrWhiteSpace(editorDirectory))
        {
            var escapedEditorDir = editorDirectory.Replace("'", "''");
            sb.AppendLine($"Write-Host 'Editor    : {escapedEditorDir}' -ForegroundColor Gray;");
        }

        sb.AppendLine("Write-Host '';");
        sb.AppendLine("Write-Host 'Useful commands:' -ForegroundColor Yellow;");
        sb.AppendLine("Write-Host '  unity --help          (view all CLI commands)' -ForegroundColor Gray;");
        sb.AppendLine("Write-Host '  unity shell           (start interactive REPL)' -ForegroundColor Gray;");
        sb.AppendLine("Write-Host '  unity doctor          (check environment & diagnostics)' -ForegroundColor Gray;");
        sb.AppendLine("Write-Host '  unity projects        (list registered projects)' -ForegroundColor Gray;");
        sb.AppendLine("Write-Host '  unity editors         (list installed editors)' -ForegroundColor Gray;");
        sb.AppendLine("Write-Host '==================================================' -ForegroundColor Cyan;");
        sb.AppendLine("Write-Host '';");

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));

        try
        {
            // Attempt 1: Windows Terminal (wt.exe)
            try
            {
                var wtInfo = new ProcessStartInfo
                {
                    FileName = "wt.exe",
                    Arguments = $"-d \"{workingDirectory}\" powershell.exe -NoExit -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                    UseShellExecute = true
                };
                Process.Start(wtInfo);
                return true;
            }
            catch
            {
                // Fall through to standard PowerShell
            }

            // Attempt 2: Standalone PowerShell
            try
            {
                var psInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
                Process.Start(psInfo);
                return true;
            }
            catch
            {
                // Fall through to Command Prompt (cmd.exe)
            }

            // Attempt 3: CMD fallback
            var cmdInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"set PATH={cliDirectory};{editorDirectory};%PATH% && echo Unity CLI Environment Ready. Type 'unity --help' for commands.\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
            Process.Start(cmdInfo);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch terminal with Unity CLI: {ex}");
            return false;
        }
    }
}
