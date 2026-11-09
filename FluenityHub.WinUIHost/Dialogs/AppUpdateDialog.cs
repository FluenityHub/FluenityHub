using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Management.Deployment;

namespace FluenityHub_WinUIHost.Dialogs;

internal static class AppUpdateDialog
{
    public static async Task ShowAsync(FrameworkElement owner, AppUpdateInfo update)
    {
        if (update.DownloadUrl is null || update.ErrorMessage is not null)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(update.ReleaseUrl));
            return;
        }
        using var cancellation = new CancellationTokenSource();
        var description = new TextBlock
        {
            Text = $"Download FluenityHub {update.LatestVersion}? The download will be checked before it can be installed.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 440
        };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(description);
        content.Children.Add(progress);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Update FluenityHub", PrimaryButtonText = "Download", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, Content = content,
            XamlRoot = owner.XamlRoot, RequestedTheme = owner.ActualTheme,
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
        };
        string? downloadedPath = null;
        var busy = false;
        var installing = false;
        var staged = false;
        dialog.CloseButtonClick += (_, args) =>
        {
            if (!busy) return;
            args.Cancel = true;
            if (installing) return;
            cancellation.Cancel();
            dialog.CloseButtonText = "Canceling...";
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy) return;
            var deferral = args.GetDeferral();
            busy = true;
            dialog.IsPrimaryButtonEnabled = false;
            error.IsOpen = false;
            try
            {
                if (downloadedPath is null)
                {
                    progress.Visibility = Visibility.Visible;
                    dialog.PrimaryButtonText = "Downloading...";
                    downloadedPath = await AppUpdateDownloadService.DownloadAsync(update, new Progress<double>(value => progress.Value = value), cancellation.Token);
                    progress.Visibility = Visibility.Collapsed;
                    var msix = downloadedPath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);
                    description.Text = msix
                        ? "The download matches the release and the original signing certificate. Windows will finish the update after you exit and reopen FluenityHub."
                        : "The portable download passed its SHA-256 check. Close FluenityHub before extracting it to your app folder. Your settings and projects are kept separately.";
                    dialog.PrimaryButtonText = msix ? "Stage update" : "Open download folder";
                    dialog.CloseButtonText = "Later";
                }
                else if (downloadedPath.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AppUpdateService.IsMsixInstallation) throw new InvalidOperationException("This update requires the installed MSIX version of FluenityHub.");
                    using var packageLock = File.Open(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await Task.Run(() => AppUpdateDownloadService.RequireExpectedIdentityAndSigner(downloadedPath, update.LatestVersion));
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                    {
                        if (!await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(downloadedPath)))
                            throw new InvalidOperationException("Windows App Installer could not open the update.");
                        dialog.Hide();
                        return;
                    }
                    try
                    {
                        installing = true;
                        dialog.CloseButtonText = "Staging...";
                        var result = await new PackageManager().AddPackageByUriAsync(new Uri(downloadedPath), new AddPackageOptions { DeferRegistrationWhenPackagesAreInUse = true });
                        if (result.ExtendedErrorCode is { HResult: not 0 } deploymentError) throw new InvalidOperationException(result.ErrorText, deploymentError);
                        staged = true;
                        description.Text = "Update ready. Exit FluenityHub from the system tray, then reopen it to finish installing.";
                        dialog.PrimaryButtonText = string.Empty;
                        dialog.CloseButtonText = "Close";
                    }
                    finally { installing = false; }
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{downloadedPath}\"", UseShellExecute = true });
                    dialog.Hide();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { dialog.Hide(); }
            catch (OperationCanceledException)
            {
                error.Message = "The update download timed out. Try again.";
                error.IsOpen = true;
                dialog.PrimaryButtonText = "Retry download";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException
                or InvalidDataException or System.Xml.XmlException or System.Text.Json.JsonException
                or InvalidOperationException or Win32Exception or COMException)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
                dialog.PrimaryButtonText = downloadedPath is null ? "Retry download" : "Try again";
                dialog.CloseButtonText = "Close";
            }
            finally
            {
                busy = false;
                progress.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = !staged;
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }
}
