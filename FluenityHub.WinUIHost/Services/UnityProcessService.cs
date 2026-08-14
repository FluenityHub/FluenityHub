using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluenityHub_WinUIHost.Services;

public static class UnityProcessService
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Determines whether Unity is actively using a project's lock file. Unity can leave a
    /// UnityLockfile behind after it closes, so the file's existence alone is not evidence
    /// that a project is still open.
    /// </summary>
    public static bool IsProjectInUse(string projectPath)
    {
        var lockFile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                lockFile,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            // Unity has the lock file open, so copying now could capture changing files.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the safety check when Windows cannot verify the file's state.
            return true;
        }
    }

    public static bool TryFocusRunningProject(string projectPath)
    {
        var lockFile = Path.Combine(projectPath, "Temp", "UnityLockfile");
        if (!IsProjectInUse(projectPath))
        {
            return false;
        }

        var projectName = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath)));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName("Unity"))
        {
            try
            {
                var windowHandle = process.MainWindowHandle;
                var windowTitle = process.MainWindowTitle;
                if (windowHandle == IntPtr.Zero ||
                    string.IsNullOrWhiteSpace(windowTitle) ||
                    !windowTitle.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ShowWindow(windowHandle, SwRestore);
                SetForegroundWindow(windowHandle);
                return true;
            }
            catch
            {
                // A Unity process may exit while the process list is being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
