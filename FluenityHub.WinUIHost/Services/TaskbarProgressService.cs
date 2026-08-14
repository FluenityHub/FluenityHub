using System.Runtime.InteropServices;

namespace FluenityHub_WinUIHost.Services;

internal enum TaskbarProgressState : uint
{
    NoProgress = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8
}

internal sealed class TaskbarProgressService : IDisposable
{
    private static readonly Guid TaskbarListClassId =
        new("56FDF344-FD6D-11D0-958A-006097C9A090");

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, TaskbarProgressState state);
    }

    private readonly nint _windowHandle;
    private ITaskbarList3? _taskbarList;
    private TaskbarProgressState _state = TaskbarProgressState.NoProgress;
    private ulong _completed;
    private ulong _total = 1000;

    public TaskbarProgressService(nint windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public void NotifyTaskbarButtonCreated()
    {
        EnsureInitialized();
        ApplyCurrentState();
    }

    private void EnsureInitialized()
    {
        if (_taskbarList is not null)
        {
            return;
        }

        try
        {
            var taskbarType = Type.GetTypeFromCLSID(TaskbarListClassId, throwOnError: true)
                ?? throw new COMException("Windows taskbar integration is unavailable.");
            _taskbarList = (ITaskbarList3)Activator.CreateInstance(taskbarType)!;
            _taskbarList.HrInit();
        }
        catch (Exception ex)
        {
            _taskbarList = null;
            System.Diagnostics.Debug.WriteLine(
                $"Taskbar progress initialization failed: {ex}");
        }
    }

    public void SetIndeterminate()
    {
        _state = TaskbarProgressState.Indeterminate;
        ApplyCurrentState();
    }

    public void SetProgress(double percentage)
    {
        _state = TaskbarProgressState.Normal;
        _completed = (ulong)Math.Clamp(Math.Round(percentage * 10), 0, 1000);
        _total = 1000;
        ApplyCurrentState();
    }

    public void SetPaused()
    {
        _state = TaskbarProgressState.Paused;
        ApplyCurrentState();
    }

    public void SetError(double? percentage)
    {
        _state = TaskbarProgressState.Error;
        _completed = (ulong)Math.Clamp(Math.Round((percentage ?? 100) * 10), 1, 1000);
        _total = 1000;
        ApplyCurrentState();
    }

    public void Clear()
    {
        _state = TaskbarProgressState.NoProgress;
        _completed = 0;
        ApplyCurrentState();
    }

    public void Reapply()
        => ApplyCurrentState();

    public void Dispose()
    {
        Clear();
        if (_taskbarList is not null && Marshal.IsComObject(_taskbarList))
        {
            Marshal.FinalReleaseComObject(_taskbarList);
        }

        _taskbarList = null;
    }

    private void TryUpdate(Action<ITaskbarList3> update)
    {
        try
        {
            if (_taskbarList is not null)
            {
                update(_taskbarList);
            }
        }
        catch (Exception ex)
        {
            // Taskbar integration is best-effort and must not interrupt installation.
            System.Diagnostics.Debug.WriteLine(
                $"Taskbar progress update failed: {ex}");
        }
    }

    private void ApplyCurrentState()
    {
        // TaskbarButtonCreated can be delivered before the WinUI window subclass is
        // attached. Installation commands are only available after the window is
        // visible, so lazily initializing here safely covers that missed-message case.
        EnsureInitialized();
        TryUpdate(taskbar =>
        {
            taskbar.SetProgressState(_windowHandle, _state);
            if (_state is TaskbarProgressState.Normal
                or TaskbarProgressState.Error
                or TaskbarProgressState.Paused)
            {
                taskbar.SetProgressValue(_windowHandle, _completed, _total);
            }
        });
    }
}
