using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluenityHub_WinUIHost.Services;

internal sealed class EditorResourceUsageService
{
    private const uint BackgroundBegin = 0x00100000;
    private const uint BackgroundEnd = 0x00200000;
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeedThrottling = 1;
    private bool _backgroundMode;
    private bool _ecoQos;

    public static bool IsEditorExecutable(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        return directory is not null
            && Path.GetFileName(executablePath).Equals("Unity.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(directory, "Data", "Managed", "UnityEditor.dll"));
    }

    public static bool IsAnyEditorRunning()
    {
        var processes = Process.GetProcessesByName("Unity");
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return !process.HasExited && process.MainModule?.FileName is string path
                        && IsEditorExecutable(path);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public void Apply(bool editorRunning, bool enabled, bool background)
    {
        var conserveResources = editorRunning && enabled;
        var backgroundMode = conserveResources && background;
        var processHandle = new IntPtr(-1); // GetCurrentProcess pseudo-handle.
        if (_backgroundMode && !backgroundMode)
        {
            if (!SetPriorityClass(processHandle, BackgroundEnd))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _backgroundMode = false;
        }

        if (!backgroundMode)
        {
            using var process = Process.GetCurrentProcess();
            var priority = conserveResources ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            if (process.PriorityClass != priority) process.PriorityClass = priority;
        }
        else if (!_backgroundMode)
        {
            if (!SetPriorityClass(processHandle, BackgroundBegin))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _backgroundMode = true;
        }

        if (_ecoQos != backgroundMode)
        {
            var power = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = backgroundMode ? ExecutionSpeedThrottling : 0u,
                StateMask = backgroundMode ? ExecutionSpeedThrottling : 0u
            };
            if (!SetProcessInformation(processHandle, ProcessPowerThrottling, ref power, (uint)Marshal.SizeOf<PowerThrottlingState>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _ecoQos = backgroundMode;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref PowerThrottlingState information, uint informationSize);
}
