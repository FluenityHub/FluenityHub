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
    private static readonly TimeSpan TokenRefreshWindow = TimeSpan.FromMinutes(5);
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

        // Best-effort token acquisition:
        // 1. If an active, non-expired token is present, use it.
        // 2. If token is near expiration and we can reach internet, attempt silent OAuth refresh.
        // 3. If no token, expired, or offline, do NOT force browser login — launch immediately in offline mode.
        UnitySharedAccessToken? sharedToken = null;
        if (UnitySharedAuthService.TryGetActiveAccessToken(out var candidateToken, out _)
            && candidateToken is not null)
        {
            if (TryGetTokenExpiration(candidateToken, out var tokenExpiration)
                && tokenExpiration > DateTimeOffset.UtcNow + TokenRefreshWindow)
            {
                sharedToken = candidateToken;
                UnityEditorLaunchDiagnostics.Write(
                    "Auth",
                    candidateToken.Expiration is double expiration
                        ? $"Active account loaded; token expiry={DateTimeOffset.FromUnixTimeMilliseconds((long)expiration):O}."
                        : "Active account loaded; token expiry was not provided.");
            }
            else if (NetworkConnectivityService.Current.CanAttemptInternet)
            {
                UnityEditorLaunchDiagnostics.Write(
                    "Auth",
                    "Access token is expired or nearing expiry; attempting background OAuth refresh.");

                try
                {
                    var (refreshedToken, refreshError) = await WaitForSharedAccessTokenAsync(cancellationToken);
                    if (refreshedToken is not null
                        && TryGetTokenExpiration(refreshedToken, out var refreshedExp)
                        && refreshedExp > DateTimeOffset.UtcNow)
                    {
                        sharedToken = refreshedToken;
                        UnityEditorLaunchDiagnostics.Write("Auth", "Unity OAuth refreshed the access token.");
                    }
                    else if (!string.IsNullOrWhiteSpace(refreshError))
                    {
                        UnityEditorLaunchDiagnostics.Write("Auth", $"OAuth refresh result: {refreshError}");
                    }
                }
                catch
                {
                    // Best-effort refresh failure should never block launching the project.
                }
            }
        }
        else if (NetworkConnectivityService.Current.CanAttemptInternet)
        {
            try
            {
                var (refreshedToken, _) = await WaitForSharedAccessTokenAsync(cancellationToken);
                if (refreshedToken is not null)
                {
                    sharedToken = refreshedToken;
                    UnityEditorLaunchDiagnostics.Write("Auth", "Unity OAuth refreshed the access token from stored credentials.");
                }
            }
            catch
            {
                // Best-effort refresh failure should never block launching the project.
            }
        }

        if (sharedToken is null)
        {
            UnityEditorLaunchDiagnostics.Write(
                "Auth",
                "No active online session; launching Editor in offline mode.");
        }

        UnityLicensingClientSession? licensingSession = null;
        UnityHubIpcGuard? hubIpcGuard = null;
        Process? editorProcess = null;
        try
        {
            if (!UnityHubIpcGuard.TryAcquire(sharedToken, out hubIpcGuard, out var hubIpcError)
                || hubIpcGuard is null)
            {
                UnityEditorLaunchDiagnostics.Write("HubIPC", $"Failed to start: {hubIpcError}");
                return new(false, hubIpcError);
            }

            UnityEditorLaunchDiagnostics.Write("HubIPC", "Server acquired.");

            var licensingResult = await UnityLicensingClientSession.StartAsync(
                editorExecutable,
                sharedToken?.Value,
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
            if (!string.IsNullOrWhiteSpace(sharedToken?.Value))
            {
                startInfo.ArgumentList.Add("-accessToken");
                startInfo.ArgumentList.Add(sharedToken.Value);
            }
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
                sharedToken is not null
                    ? "Unity Editor is starting with the signed-in Unity account."
                    : "Unity Editor is starting in offline mode.",
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

    private static async Task<(UnitySharedAccessToken? Token, string ErrorMessage)>
        WaitForSharedAccessTokenAsync(CancellationToken cancellationToken)
    {
        // First, attempt silent background OAuth token refresh
        var (refreshedToken, refreshError) = await UnitySharedAuthService.RefreshOAuthTokenAsync(cancellationToken);
        if (refreshedToken is not null)
        {
            return (refreshedToken, string.Empty);
        }

        const int maximumAttempts = 10;
        var lastError = !string.IsNullOrWhiteSpace(refreshError)
            ? refreshError
            : "The active Unity CLI credential could not be read. Sign in again and retry.";

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (UnitySharedAuthService.TryGetActiveAccessToken(out var token, out var errorMessage)
                && token is not null)
            {
                return (token, string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                lastError = errorMessage;
            }

            if (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }

        return (null, lastError);
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

    private static bool TryGetTokenExpiration(
        UnitySharedAccessToken token,
        out DateTimeOffset expiration)
    {
        expiration = default;
        if (token.Expiration is not double value
            || double.IsNaN(value)
            || double.IsInfinity(value)
            || value < long.MinValue
            || value > long.MaxValue)
        {
            return false;
        }

        try
        {
            expiration = DateTimeOffset.FromUnixTimeMilliseconds((long)value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
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
