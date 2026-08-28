using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluenityHub_WinUIHost.Helpers;
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
    private readonly UnityCliToolService _cliToolService = new();

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
        UnitySharedAccessToken? sharedToken = null;
        if (settings.LaunchEditorInOfflineMode)
        {
            UnityEditorLaunchDiagnostics.Write(
                "Auth",
                "Offline Editor mode is enabled in Unity license settings; the active account will not be passed to the Editor.");
        }
        else if (UnitySharedAuthService.TryGetActiveAccessToken(out var candidateToken, out _)
            && candidateToken is not null
            && TryGetTokenExpiration(candidateToken, out var tokenExpiration)
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
                "Unity account session is missing or expired; renewing it through Unity CLI before launch.");

            var (refreshedToken, refreshError) = await EnsureSharedAccessTokenAsync(cancellationToken);
            if (refreshedToken is null)
            {
                UnityEditorLaunchDiagnostics.Write("Auth", $"Sign-in failed: {refreshError}");
                return new(
                    false,
                    string.IsNullOrWhiteSpace(refreshError)
                        ? "Unity sign-in was not completed. Sign in from FluenityHub and retry."
                        : refreshError);
            }

            sharedToken = refreshedToken;
            UnityEditorLaunchDiagnostics.Write("Auth", "Unity account session is ready.");
        }
        else
        {
            UnityEditorLaunchDiagnostics.Write(
                "Auth",
                "No active account session and Windows is offline; launch rejected by Unity license settings.");
            return new(
                false,
                "You're offline and Unity is not signed in. Enable 'Launch Editor in offline mode' in Settings > Unity licenses, or connect to the internet and sign in.");
        }

        if (HasEditorLicensingClient(editorExecutable))
        {
            var cliLaunchResult = await TryLaunchWithUnityCliAsync(
                editorExecutable,
                projectPath,
                targetPlatform,
                extraArguments,
                cancellationToken);
            if (cliLaunchResult is not null)
            {
                return cliLaunchResult;
            }
        }
        else
        {
            UnityEditorLaunchDiagnostics.Write(
                "Launch",
                "Editor-local licensing client is missing; using the shared licensing-client launch path.");
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

    private async Task<UnityEditorLaunchResult?> TryLaunchWithUnityCliAsync(
        string editorExecutable,
        string projectPath,
        string? targetPlatform,
        string? extraArguments,
        CancellationToken cancellationToken)
    {
        var cliExecutable = await _cliToolService.GetVerifiedExecutablePathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(cliExecutable) || !File.Exists(cliExecutable))
        {
            UnityEditorLaunchDiagnostics.Write(
                "Launch",
                "Unity CLI is unavailable; using the direct Editor launch fallback.");
            return null;
        }

        var normalizedEditorPath = Path.GetFullPath(editorExecutable);
        var existingEditorProcessIds = GetEditorProcessIds(normalizedEditorPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = cliExecutable,
            WorkingDirectory = Path.GetDirectoryName(cliExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--non-interactive");
        startInfo.ArgumentList.Add("open");
        startInfo.ArgumentList.Add(Path.GetFullPath(projectPath));
        startInfo.ArgumentList.Add("--editor-path");
        startInfo.ArgumentList.Add(normalizedEditorPath);

        if (!string.IsNullOrWhiteSpace(targetPlatform))
        {
            startInfo.ArgumentList.Add("--build-target");
            startInfo.ArgumentList.Add(targetPlatform);
        }

        if (!string.IsNullOrWhiteSpace(extraArguments))
        {
            startInfo.ArgumentList.Add("--args");
            startInfo.ArgumentList.Add(extraArguments);
        }

        UnityEditorLaunchDiagnostics.Write(
            "Launch",
            "Delegating Editor startup and account identity to the official Unity CLI.");

        try
        {
            using var cliProcess = new Process { StartInfo = startInfo };
            if (!cliProcess.Start())
            {
                return new(false, "Unity CLI could not be started.");
            }

            var standardOutputBuilder = new StringBuilder();
            var standardErrorBuilder = new StringBuilder();
            var outputSync = new object();
            cliProcess.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    lock (outputSync)
                    {
                        standardOutputBuilder.AppendLine(eventArgs.Data);
                    }
                }
            };
            cliProcess.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    lock (outputSync)
                    {
                        standardErrorBuilder.AppendLine(eventArgs.Data);
                    }
                }
            };
            cliProcess.BeginOutputReadLine();
            cliProcess.BeginErrorReadLine();

            using var commandTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            commandTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await cliProcess.WaitForExitAsync(commandTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(cliProcess);
                return new(false, "Unity CLI took too long to open the project.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            TryCancelOutputRead(cliProcess);

            string standardOutput;
            string standardError;
            lock (outputSync)
            {
                standardOutput = standardOutputBuilder.ToString();
                standardError = standardErrorBuilder.ToString();
            }

            if (cliProcess.ExitCode != 0)
            {
                var message = GetUnityCliErrorMessage(standardOutput, standardError);
                UnityEditorLaunchDiagnostics.Write(
                    "Launch",
                    $"Unity CLI open failed; code={cliProcess.ExitCode}; message={message}");
                return new(false, message);
            }

            var editorProcess = await WaitForNewEditorProcessAsync(
                normalizedEditorPath,
                existingEditorProcessIds,
                cancellationToken);
            if (editorProcess is null)
            {
                UnityEditorLaunchDiagnostics.Write(
                    "Launch",
                    "Unity CLI exited successfully, but no Editor process appeared; using the direct launch fallback.");
                return null;
            }

            UnityEditorLaunchDiagnostics.Write(
                "Launch",
                $"Unity CLI started Editor pid={editorProcess.Id}.");
            return new(
                true,
                "Unity Editor is starting with the persistent Unity CLI account.",
                editorProcess);
        }
        catch (OperationCanceledException)
        {
            return new(false, "Opening the Unity project was canceled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            UnityEditorLaunchDiagnostics.Write(
                "Launch",
                $"Unity CLI open failed: {ex.GetType().Name}: {ex.Message}");
            return new(false, $"Unable to open Unity Editor through Unity CLI: {ex.Message}");
        }
    }

    private static void TryCancelOutputRead(Process process)
    {
        try
        {
            process.CancelOutputRead();
        }
        catch (InvalidOperationException)
        {
            // The parent command may have already closed its output stream.
        }

        try
        {
            process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
            // The parent command may have already closed its error stream.
        }
    }
    private static bool HasEditorLicensingClient(string editorExecutable)
    {
        var editorDirectory = Path.GetDirectoryName(Path.GetFullPath(editorExecutable));
        return editorDirectory is not null
            && File.Exists(Path.Combine(
                editorDirectory,
                "Data",
                "Resources",
                "Licensing",
                "Client",
                "Unity.Licensing.Client.exe"));
    }
    private static HashSet<int> GetEditorProcessIds(string editorExecutable)
    {
        var processIds = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("Unity"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(
                        process.MainModule?.FileName,
                        editorExecutable,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        processIds.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Processes that are exiting or inaccessible are not launch candidates.
                }
            }
        }

        return processIds;
    }

    private static async Task<Process?> WaitForNewEditorProcessAsync(
        string editorExecutable,
        HashSet<int> existingProcessIds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            foreach (var process in Process.GetProcessesByName("Unity"))
            {
                try
                {
                    if (!existingProcessIds.Contains(process.Id)
                        && string.Equals(
                            process.MainModule?.FileName,
                            editorExecutable,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return process;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // The process may have exited between enumeration and inspection.
                }

                process.Dispose();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return null;
    }

    private static string GetUnityCliErrorMessage(string standardOutput, string standardError)
    {
        foreach (var source in new[] { standardError, standardOutput })
        {
            var start = source.IndexOf('{');
            var end = source.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(source[start..(end + 1)]);
                if (document.RootElement.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array
                    && errors.GetArrayLength() > 0
                    && errors[0].TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(message.GetString()))
                {
                    return SensitiveDataRedactor.Redact(message.GetString()!);
                }
            }
            catch (JsonException)
            {
                // Fall back to a safe non-JSON line below.
            }
        }

        var fallback = new[] { standardError, standardOutput }
            .SelectMany(value => value.Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault(line =>
                !line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith('{')
                && !line.StartsWith('}'));

        return string.IsNullOrWhiteSpace(fallback)
            ? "Unity CLI could not open the project."
            : SensitiveDataRedactor.Redact(fallback);
    }
    public static string GetSandboxDirectory(string editorVersion)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluenityHub",
            "SandboxProjects",
            $"Unity_{editorVersion}");
    }

    private async Task<(UnitySharedAccessToken? Token, string ErrorMessage)>
        EnsureSharedAccessTokenAsync(CancellationToken cancellationToken)
    {
        var (refreshedToken, refreshError) =
            await UnitySharedAuthService.RefreshOAuthTokenAsync(cancellationToken);
        if (refreshedToken is not null && UnitySharedAuthService.IsAccessTokenUsable(refreshedToken))
        {
            return (refreshedToken, string.Empty);
        }

        if (UnitySharedAuthService.TryGetActiveAccessToken(out var storedToken, out _)
            && storedToken is not null
            && UnitySharedAuthService.IsAccessTokenUsable(storedToken))
        {
            return (storedToken, string.Empty);
        }

        UnityEditorLaunchDiagnostics.Write(
            "Auth",
            "Silent renewal was unavailable; starting Unity CLI browser sign-in.");
        var authState = await _cliAuthService.LoginAsync(cancellationToken);
        if (!authState.IsLoggedIn)
        {
            return (
                null,
                string.IsNullOrWhiteSpace(authState.Message)
                    ? refreshError
                    : authState.Message);
        }

        const int maximumAttempts = 25;
        var lastError = string.IsNullOrWhiteSpace(refreshError)
            ? "Unity sign-in completed, but its credential was not synchronized. Retry sign-in."
            : refreshError;

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            if (UnitySharedAuthService.TryGetActiveAccessToken(out var token, out var errorMessage)
                && token is not null
                && UnitySharedAuthService.IsAccessTokenUsable(token))
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
