using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FluenityHub_WinUIHost.Helpers;

/// <summary>
/// Manages a single Win32 Shell_NotifyIcon in the Windows system tray.
/// Call Show() to add the icon, Hide() to remove it, Dispose() to clean up.
/// Only one icon is ever created (guarded by _isCreated).
/// </summary>
public sealed class NativeTrayIcon : IDisposable
{
#region Win32 P/Invoke & Structures

    private const uint WM_USER = 0x0400;
    public const uint WM_TRAYICON = WM_USER + 0x100;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_GRAYED = 0x00000001;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint ICON_ID = 1001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, ulong uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, ulong uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, ulong uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

#endregion

    private readonly IntPtr _hwnd;
    private readonly SUBCLASSPROC _subclassProc; // Retain delegate reference to prevent GC collection during Win32 subclassing
    private readonly uint _taskbarButtonCreatedMessage;
    private bool _isCreated;
    private IntPtr _hIcon;
    private bool _disposed;

    public Action? OnSettingsClicked { get; set; }
    public Action? OnExitClicked { get; set; }
    public Action? OnTaskbarButtonCreated { get; set; }
    public Action<Models.UnityProjectInfo>? OnProjectClicked { get; set; }
    public Func<List<Models.UnityProjectInfo>>? GetRecentProjects { get; set; }

    public NativeTrayIcon(IntPtr hwnd, Action? onTaskbarButtonCreated = null)
    {
        _hwnd = hwnd;
        _subclassProc = WindowSubclassProc;
        OnTaskbarButtonCreated = onTaskbarButtonCreated;
        _taskbarButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
        SetWindowSubclass(_hwnd, _subclassProc, ICON_ID, IntPtr.Zero);
    }

    /// <summary>
    /// Adds the tray icon to the Windows notification area.
    /// Safe to call multiple times — only the first call creates the icon.
    /// </summary>
    public void Show(string tooltip)
    {
        if (_isCreated || _disposed) return;

        try
        {
            // Load .ico from Assets folder, fall back to default app icon
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                _hIcon = LoadImage(IntPtr.Zero, iconPath, 1 /* IMAGE_ICON */, 16, 16, 0x00000010 /* LR_LOADFROMFILE */);
            }
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
            }

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = ICON_ID,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = tooltip
            };

            _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NativeTrayIcon.Show error: {ex}");
        }
    }

    /// <summary>
    /// Removes the tray icon from the Windows notification area.
    /// Safe to call multiple times.
    /// </summary>
    public void Hide()
    {
        if (!_isCreated) return;

        try
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = ICON_ID
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
        }
        catch
        {
            // Best-effort cleanup
        }
        finally
        {
            _isCreated = false;
        }
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        try
        {
            var recentProjects = GetRecentProjects?.Invoke() ?? new List<Models.UnityProjectInfo>();
            var projectMap = new Dictionary<uint, Models.UnityProjectInfo>();

            if (recentProjects.Count > 0)
            {
                AppendMenu(hMenu, MF_GRAYED, UIntPtr.Zero, "Recent Projects");
                uint cmdId = 2001;
                foreach (var project in recentProjects.Take(5))
                {
                    projectMap[cmdId] = project;
                    AppendMenu(hMenu, MF_STRING, (UIntPtr)cmdId, $"   {project.Title} ({project.Version})");
                    cmdId++;
                }
                AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            }

            AppendMenu(hMenu, MF_STRING, (UIntPtr)3001, "Settings");
            AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            AppendMenu(hMenu, MF_STRING, (UIntPtr)3002, "Exit");

            // Must bring app window to foreground before TrackPopupMenu so clicking outside closes the menu
            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var selectedCmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, 0, _hwnd, IntPtr.Zero);

            if (selectedCmd == 3001)
            {
                OnSettingsClicked?.Invoke();
            }
            else if (selectedCmd == 3002)
            {
                OnExitClicked?.Invoke();
            }
            else if (projectMap.TryGetValue(selectedCmd, out var projectInfo))
            {
                OnProjectClicked?.Invoke(projectInfo);
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, ulong uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            var msg = (uint)lParam.ToInt64();
            if (msg is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                MainWindow.Instance?.RestoreWindow();
            }
            else if (msg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
        }
        else if (_taskbarButtonCreatedMessage != 0
                 && uMsg == _taskbarButtonCreatedMessage)
        {
            OnTaskbarButtonCreated?.Invoke();
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Hide();
        RemoveWindowSubclass(_hwnd, _subclassProc, ICON_ID);
    }
}
