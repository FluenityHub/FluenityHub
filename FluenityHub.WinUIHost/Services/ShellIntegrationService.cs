using Microsoft.Win32;

namespace FluenityHub_WinUIHost.Services;

public static class ShellIntegrationService
{
    private const string LegacyDirectoryKey = @"Software\Classes\Directory\shell\FluenityHub";
    private const string LegacyBackgroundKey = @"Software\Classes\Directory\Background\shell\FluenityHub";
    private const string CommandSettingsKey = @"Software\FluenityHub\ExplorerCommand";
    private const string CommandEnabledValue = "Enabled";

    public static bool IsModernExplorerContextMenuAvailable
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && HasPackageIdentity();

    public static void SetExplorerContextMenuEnabled(bool enabled)
    {
        if (enabled && !IsModernExplorerContextMenuAvailable)
        {
            throw new InvalidOperationException(
                "File Explorer integration requires the MSIX version of FluenityHub on Windows 11.");
        }

        Registry.CurrentUser.DeleteSubKeyTree(LegacyDirectoryKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(LegacyBackgroundKey, throwOnMissingSubKey: false);

        using var settingsKey = Registry.CurrentUser.CreateSubKey(CommandSettingsKey);
        settingsKey.SetValue(CommandEnabledValue, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static bool HasPackageIdentity()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(Windows.ApplicationModel.Package.Current.Id.FullName);
        }
        catch
        {
            return false;
        }
    }
}