using Microsoft.Win32;

namespace FluenityHub_WinUIHost.Services;

public static class ShellIntegrationService
{
    private const string DirectoryKey = @"Software\Classes\Directory\shell\FluenityHub";
    private const string BackgroundKey = @"Software\Classes\Directory\Background\shell\FluenityHub";

    public static void SetExplorerContextMenuEnabled(bool enabled)
    {
        if (enabled)
        {
            RegisterContextMenu(DirectoryKey);
            RegisterContextMenu(BackgroundKey);
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(DirectoryKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(BackgroundKey, throwOnMissingSubKey: false);
    }

    private static void RegisterContextMenu(string keyPath)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("The FluenityHub executable path could not be resolved.");
        }

        using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
        shellKey.SetValue(null, "Open with FluenityHub");
        shellKey.SetValue("Icon", $"\"{executablePath}\"");

        using var commandKey = shellKey.CreateSubKey("command");
        commandKey.SetValue(null, $"\"{executablePath}\" --project \"%V\"");
    }
}
