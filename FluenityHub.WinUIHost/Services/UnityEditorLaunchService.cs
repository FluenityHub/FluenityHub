using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record UnityEditorLaunchResult(
    bool Succeeded,
    string Message,
    Process? EditorProcess = null);

/// <summary>
/// Opens Unity Editors using the same account and Licensing Client handoff used
/// by Unity Hub, without starting the Unity Hub application.
/// </summary>
public sealed class UnityEditorLaunchService
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly UnityCliAuthService _cliAuthService = new();

    public async Task<UnityEditorLaunchResult> LaunchBlankEditorAsync(
        UnityEditorInfo editor,
        CancellationToken cancellationToken = default)
    {
        var sandboxPath = GetSandboxDirectory(editor.Version);

        var settings = _settingsStore.Load();
        if (settings.AutoResetSandboxOnClose)
        {
            ResetSandboxWorkspace(sandboxPath, editor.Version);
        }
        else
        {
            EnsureSandboxProjectExists(sandboxPath, editor.Version);
        }

        return await LaunchProjectAsync(
            editor.ExecutablePath,
            sandboxPath,
            editor.Version,
            cancellationToken: cancellationToken);
    }

    public bool ResetSandbox(string editorVersion)
    {
        var sandboxPath = GetSandboxDirectory(editorVersion);
        return ResetSandboxWorkspace(sandboxPath, editorVersion);
    }

    public async Task<UnityEditorLaunchResult> LaunchProjectAsync(
        string editorExecutable,
        string projectPath,
        string? editorVersion = null,
        string? targetPlatform = null,
        string? extraArguments = null,
        CancellationToken cancellationToken = default)
    {
        UnityEditorLaunchDiagnostics.Begin(editorExecutable, projectPath);

        if (string.IsNullOrWhiteSpace(editorExecutable) || !File.Exists(editorExecutable))
        {
            UnityEditorLaunchDiagnostics.Write("Launch", "Rejected: Editor executable was not found.");
            return new(false, $"Unity Editor executable not found at: {editorExecutable}");
        }

        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            UnityEditorLaunchDiagnostics.Write("Launch", "Rejected: project directory was not found.");
            return new(false, $"Project path not found: {projectPath}");
        }
        var settings = _settingsStore.Load();
        if (!settings.LaunchEditorInOfflineMode)
        {
            try
            {
                return await LaunchWithCliAsync(editorExecutable, projectPath, targetPlatform, extraArguments, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new(false, "Opening the Unity project was canceled.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            {
                UnityEditorLaunchDiagnostics.Write("Launch", $"Unity CLI handoff failed: {ex.GetType().Name}");
                return new(false, $"Unable to launch Unity Editor: {ex.Message}");
            }
        }
        UnityEditorLaunchDiagnostics.Write("Auth", "Offline Editor mode; no account credential is passed to the Editor.");

        UnityLicensingClientSession? licensingSession = null;
        UnityHubIpcGuard? hubIpcGuard = null;
        Process? editorProcess = null;
        try
        {
            if (!UnityHubIpcGuard.TryAcquire(null, out hubIpcGuard, out var hubIpcError)
                || hubIpcGuard is null)
            {
                UnityEditorLaunchDiagnostics.Write("HubIPC", $"Failed to start: {hubIpcError}");
                return new(false, hubIpcError);
            }

            UnityEditorLaunchDiagnostics.Write("HubIPC", "Server acquired.");

            var licensingResult = await UnityLicensingClientSession.StartAsync(
                editorExecutable,
                null,
                cancellationToken);
            licensingSession = licensingResult.Session;
            if (licensingSession is null)
            {
                UnityEditorLaunchDiagnostics.Write("Licensing", $"Failed to start: {licensingResult.ErrorMessage}");
                return new(false, licensingResult.ErrorMessage);
            }

            UnityEditorLaunchDiagnostics.Write(
                "Licensing",
                $"Client ready on {licensingSession.EditorPipeName}.");

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(editorExecutable),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(editorExecutable))!,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-projectPath");
            startInfo.ArgumentList.Add(Path.GetFullPath(projectPath));
            startInfo.ArgumentList.Add("-acceptSoftwareTermsForThisRunOnly");
            startInfo.ArgumentList.Add("-useHub");
            startInfo.ArgumentList.Add("-hubIPC");
            startInfo.ArgumentList.Add("-cloudEnvironment");
            startInfo.ArgumentList.Add("production");
            startInfo.ArgumentList.Add("-hubSessionId");
            startInfo.ArgumentList.Add(Guid.NewGuid().ToString());
            startInfo.ArgumentList.Add("-licensingIpc");
            startInfo.ArgumentList.Add(licensingSession.EditorPipeName);

            if (!string.IsNullOrWhiteSpace(targetPlatform))
            {
                startInfo.ArgumentList.Add("-buildTarget");
                startInfo.ArgumentList.Add(targetPlatform);
            }

            foreach (var argument in SplitCommandLine(extraArguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            editorProcess = new Process { StartInfo = startInfo };
            if (!editorProcess.Start())
            {
                UnityEditorLaunchDiagnostics.Write("Launch", "Process.Start returned false.");
                return new(false, "Unity Editor could not be started.");
            }

            UnityEditorLaunchDiagnostics.Write("Launch", $"Editor process started; pid={editorProcess.Id}.");

            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            if (editorProcess.HasExited)
            {
                var exitCode = editorProcess.ExitCode;
                editorProcess.Dispose();
                editorProcess = null;
                UnityEditorLaunchDiagnostics.Write("Launch", $"Editor exited during startup; code={exitCode}.");
                return new(false, $"Unity Editor stopped during startup (exit code {exitCode}).");
            }

            licensingSession.AttachToEditor(editorProcess);
            licensingSession = null;
            hubIpcGuard.AttachToEditor(editorProcess);
            hubIpcGuard = null;
            UnityEditorLaunchDiagnostics.Write("Launch", "Editor passed the startup check; services attached.");
            return new(
                true,
                "Unity Editor is starting in offline mode.",
                editorProcess);
        }
        catch (OperationCanceledException)
        {
            UnityEditorLaunchDiagnostics.Write("Launch", "Canceled.");
            TryTerminate(editorProcess);
            return new(false, "Opening the Unity project was canceled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UnityEditorLaunchDiagnostics.Write("Launch", $"Failed: {ex.GetType().Name}: {ex.Message}");
            TryTerminate(editorProcess);
            return new(false, $"Unable to launch Unity Editor: {ex.Message}");
        }
        finally
        {
            licensingSession?.Dispose();
            hubIpcGuard?.Dispose();
        }
    }

    public static string GetSandboxDirectory(string editorVersion)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluenityHub",
            "SandboxProjects",
            $"Unity_{editorVersion}");
    }

    private async Task<UnityEditorLaunchResult> LaunchWithCliAsync(
        string editorExecutable, string projectPath, string? targetPlatform,
        string? extraArguments, CancellationToken cancellationToken)
    {
        if (UnityAccountConnectionState.IsDisconnected)
            return new(false, "Sign in from the account menu, or enable offline Editor mode in Settings > Unity licenses.");

        var account = await _cliAuthService.GetStatusAsync(cancellationToken);
        if (!account.IsLoggedIn) return new(false, account.Message);
        var arguments = new List<string> { "open", Path.GetFullPath(projectPath), "--editor-path", Path.GetFullPath(editorExecutable) };
        if (!string.IsNullOrWhiteSpace(targetPlatform))
        {
            arguments.Add("--build-target");
            arguments.Add(targetPlatform);
        }
        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            arguments.Add("--args");
            arguments.Add(extraArguments);
        }
        var result = await UnityCliCommandService.RunAsync(arguments, cancellationToken, TimeSpan.FromMinutes(2));
        if (!result.Succeeded) return new(false, result.Message);
        Process? editor = null;
        if (result.Data.ValueKind == System.Text.Json.JsonValueKind.Object
            && result.Data.TryGetProperty("launchedPid", out var pid) && pid.TryGetInt32(out var id))
        {
            try { editor = Process.GetProcessById(id); }
            catch (ArgumentException) { return new(false, "Unity Editor stopped during startup."); }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(editor);
                editor.Dispose();
                throw;
            }
            if (editor.HasExited)
            {
                var exitCode = editor.ExitCode;
                editor.Dispose();
                return new(false, $"Unity Editor stopped during startup (exit code {exitCode}). Check the Editor log for details.");
            }
        }
        UnityEditorLaunchDiagnostics.Write("Launch", "Unity CLI completed the account and organization handoff.");
        return new(true, "Unity Editor is starting.", editor);
    }


    private static IReadOnlyList<string> SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var argv = CommandLineToArgvW($"FluenityHub {commandLine}", out var argumentCount);
        if (argv == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to parse Unity launch arguments.");
        }

        try
        {
            var arguments = new List<string>(Math.Max(0, argumentCount - 1));
            for (var index = 1; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments.Add(Marshal.PtrToStringUni(valuePointer) ?? string.Empty);
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static void TryTerminate(Process? process)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }



    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static bool ResetSandboxWorkspace(string path, string editorVersion)
    {
        try
        {
            var assetsDir = Path.Combine(path, "Assets");
            if (Directory.Exists(assetsDir))
            {
                Directory.Delete(assetsDir, recursive: true);
            }

            EnsureSandboxProjectExists(path, editorVersion);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSandboxProjectExists(string path, string editorVersion)
    {
        Directory.CreateDirectory(Path.Combine(path, "Assets"));
        Directory.CreateDirectory(Path.Combine(path, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(path, "Packages"));

        var versionFile = Path.Combine(path, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile))
        {
            File.WriteAllText(versionFile, $"m_EditorVersion: {editorVersion}\r\nm_EditorVersionWithRevision: {editorVersion}\r\n");
        }

        var manifestFile = Path.Combine(path, "Packages", "manifest.json");
        if (!File.Exists(manifestFile))
        {
            File.WriteAllText(manifestFile, $"{{\"dependencies\": {{}}}}{Environment.NewLine}");
        }
    }
}
