using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FluenityHub_WinUIHost.Services;

public static class JumpListService
{
    public const string AppUserModelId = "Fluenity.FluenityHub";

    private static readonly object SyncLock = new();
    private static bool _isUpdating;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [ComImport]
    [Guid("77F10CF0-3DB5-4966-B520-B7C54FD35ED6")]
    [ClassInterface(ClassInterfaceType.None)]
    private class DestinationList { }

    [ComImport]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        [PreserveSig]
        int BeginList(out uint pcMinSlots, [In] ref Guid riid, [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig]
        int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, [MarshalAs(UnmanagedType.IUnknown)] object poa);
        void AppendKnownCategory(int category);
        [PreserveSig]
        int AddUserTasks([MarshalAs(UnmanagedType.IUnknown)] object poa);
        [PreserveSig]
        int CommitList();
        void GetRemovedDestinations([In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void AbortList();
    }

    [ComImport]
    [Guid("2D3468C1-36A7-43B6-AC24-D3F02FD9607A")]
    [ClassInterface(ClassInterfaceType.None)]
    private class EnumerableObjectCollection { }

    [ComImport]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        uint GetCount();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetAt(uint uiIndex, [In] ref Guid riid);
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object pvObject);
        void AddFromArray([MarshalAs(UnmanagedType.IUnknown)] object poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid guid, uint id) { fmtid = guid; pid = id; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;

        public static PropVariant FromString(string val)
        {
            var pv = new PropVariant();
            pv.vt = 31; // VT_LPWSTR
            pv.pwszVal = Marshal.StringToCoTaskMemUni(val);
            return pv;
        }

        public void Free()
        {
            if (pwszVal != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pwszVal);
                pwszVal = IntPtr.Zero;
            }
        }
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue([In] ref PropertyKey key, [Out] out PropVariant pv);
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant pv);
        void Commit();
    }

    private static readonly PropertyKey PKEY_Title = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    private static readonly PropertyKey PKEY_AppUserModel_ID = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>
    /// Sets the Explicit AppUserModelID for the current process and ensures
    /// the Start Menu shortcut has the AppUserModelID embedded so Windows Taskbar
    /// and Start Menu display Jump Lists.
    /// </summary>
    public static void InitializeAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            EnsureStartMenuShortcut();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InitializeAppUserModelId failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures that a shortcut with FluenityHub's AppUserModelID exists in the
    /// user's Start Menu Programs folder so the Windows Start Menu displays Jump Lists.
    /// </summary>
    public static void EnsureStartMenuShortcut()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                processPath = Path.Combine(AppContext.BaseDirectory, "FluenityHub.exe");
            }

            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");

            if (!Directory.Exists(startMenuDir))
            {
                Directory.CreateDirectory(startMenuDir);
            }

            var shortcutPath = Path.Combine(startMenuDir, "FluenityHub.lnk");
            if (File.Exists(shortcutPath))
            {
                try { File.Delete(shortcutPath); } catch { }
            }

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(processPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory);
            link.SetDescription("FluenityHub - Native Unity Hub Alternative");
            link.SetIconLocation(processPath, 0);

            var propStore = (IPropertyStore)link;
            var key = PKEY_AppUserModel_ID;
            var pv = PropVariant.FromString(AppUserModelId);
            propStore.SetValue(ref key, ref pv);
            propStore.Commit();
            pv.Free();

            var persistFile = (IPersistFile)link;
            persistFile.Save(shortcutPath, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnsureStartMenuShortcut failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the AppUserModelID on a window HWND to bind the window to FluenityHub's Taskbar icon.
    /// </summary>
    public static void SetWindowAppUserModelId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var iid = IID_IPropertyStore;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var propStore);
            if (hr == 0 && propStore is not null)
            {
                var key = PKEY_AppUserModel_ID;
                var pv = PropVariant.FromString(AppUserModelId);
                propStore.SetValue(ref key, ref pv);
                propStore.Commit();
                pv.Free();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SetWindowAppUserModelId failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes the Windows Jump List with standard Tasks (New project, Install Editor)
    /// and recent Unity projects.
    /// </summary>
    public static async Task RefreshAsync()
    {
        await Task.Run(() =>
        {
            lock (SyncLock)
            {
                if (_isUpdating) return;
                _isUpdating = true;
            }

            try
            {
                InitializeAppUserModelId();

                var processPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
                {
                    processPath = Path.Combine(AppContext.BaseDirectory, "FluenityHub.exe");
                }

                var destList = (ICustomDestinationList)new DestinationList();
                destList.SetAppID(AppUserModelId);

                var iid = IID_IUnknown;
                int hr = destList.BeginList(out uint minSlots, ref iid, out _);
                if (hr != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"ICustomDestinationList.BeginList failed with hr={hr:X8}");
                    return;
                }

                var taskCollection = (IObjectCollection)new EnumerableObjectCollection();

                // 1. Primary Task: New project
                AddShellLink(
                    taskCollection,
                    processPath,
                    "--action new-project",
                    "New project",
                    "Create a new Unity project wizard");

                // 2. Primary Task: Install Editor
                AddShellLink(
                    taskCollection,
                    processPath,
                    "--action install-editor",
                    "Install Editor",
                    "Browse and install Unity Editor releases");

                // 3. Recent Projects as quick task entries
                try
                {
                    var projectService = new UnityHubProjectService();
                    var recentProjects = projectService.GetRecentProjects(
                            repairProjectsFile: false,
                            resolveProductNames: false)
                        .Where(p => !string.IsNullOrWhiteSpace(p.Path) && Directory.Exists(p.Path))
                        .Take(6);

                    foreach (var project in recentProjects)
                    {
                        var title = !string.IsNullOrWhiteSpace(project.Title)
                            ? project.Title
                            : Path.GetFileName(Path.TrimEndingDirectorySeparator(project.Path));

                        if (string.IsNullOrWhiteSpace(title)) continue;

                        AddShellLink(
                            taskCollection,
                            processPath,
                            $"--project \"{project.Path}\"",
                            title,
                            project.Path);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to enumerate recent projects for JumpList: {ex.Message}");
                }

                int hrTasks = destList.AddUserTasks(taskCollection);
                if (hrTasks == 0)
                {
                    destList.CommitList();
                }
                else
                {
                    destList.AbortList();
                    System.Diagnostics.Debug.WriteLine($"ICustomDestinationList.AddUserTasks failed with hr={hrTasks:X8}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JumpListService.RefreshAsync failed: {ex}");
            }
            finally
            {
                lock (SyncLock)
                {
                    _isUpdating = false;
                }
            }
        });
    }

    private static void AddShellLink(
        IObjectCollection collection,
        string executablePath,
        string arguments,
        string title,
        string description)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(executablePath);
            link.SetArguments(arguments);
            link.SetDescription(description);
            link.SetIconLocation(executablePath, 0);

            var propStore = (IPropertyStore)link;
            var key = PKEY_Title;
            var pv = PropVariant.FromString(title);
            propStore.SetValue(ref key, ref pv);
            propStore.Commit();
            pv.Free();

            collection.AddObject(link);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AddShellLink '{title}' failed: {ex.Message}");
        }
    }
}
