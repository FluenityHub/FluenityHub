using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityEditorInstallationService
{
    private static readonly Regex ItemProgressMessagePattern = new(
        "^(?:Downloading|Installing)\\s+(?<name>.+?)(?:\\.{3}|…)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TransientInstallerRetryDelay = TimeSpan.FromSeconds(2);
    private const int MaximumInstallAttempts = 4;
    private const long MinimumFreeDiskSpaceBytes = 10L * 1024 * 1024 * 1024; // 10 GB
    private const long MinimumSystemTempFreeDiskSpaceBytes = 20L * 1024 * 1024 * 1024; // 20 GB

    public async Task<UnityModuleInstallResult> InstallAsync(
        string version,
        string installRoot,
        string? revision,
        IReadOnlyCollection<string> moduleIds,
        bool resumeInterruptedDownload,
        IProgress<UnityModuleInstallProgress>? progress,
        Action<string>? outputObserver,
        CancellationToken cancellationToken)
    {
        var cliPath = await new UnityCliToolService().GetVerifiedExecutablePathAsync(cancellationToken);
        if (cliPath is null)
        {
            return new(false, "Install the Unity CLI component before installing an Editor.", string.Empty);
        }

        try
        {
            Directory.CreateDirectory(installRoot);
        }
        catch (UnauthorizedAccessException)
        {
            return new(
                false,
                $"FluenityHub cannot write to '{installRoot}'. Choose a writable Unity Editor install location in Settings.",
                string.Empty);
        }
        catch (IOException ex)
        {
            return new(
                false,
                $"The Unity Editor install location could not be prepared. {ex.Message}",
                string.Empty);
        }

        var installLocationError = ValidateWritableLocation(installRoot);
        if (installLocationError is not null)
        {
            return new(
                false,
                $"FluenityHub cannot write to '{installRoot}'. Choose a writable Unity Editor install location in Settings. "
                + installLocationError,
                installLocationError);
        }

        var downloadLocation = new UnityHubLocationSettingsService().GetDownloadLocation();
        var downloadLocationError = ValidateWritableLocation(downloadLocation);
        if (downloadLocationError is not null)
        {
            return new(
                false,
                $"Unity's download location is not writable: '{downloadLocation}'. "
                + "Choose a writable Downloads location in Settings before installing an Editor. "
                + downloadLocationError,
                downloadLocationError);
        }

        var diskSpaceError = GetEditorInstallStorageError(installRoot, downloadLocation);
        if (diskSpaceError is not null)
        {
            return new(false, diskSpaceError, string.Empty);
        }

        try
        {
            // Unity CLI and Unity Hub read the same secondary install-location
            // preference. Writing it directly avoids a redundant CLI process
            // that can wait on Unity Hub's shared state before any install
            // progress is emitted.
            new UnityHubLocationSettingsService().SetInstallLocation(installRoot);
            outputObserver?.Invoke($"All Unity Editors will be installed to {installRoot}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(
                false,
                $"FluenityHub could not save the Editor install location. {ex.Message}",
                ex.ToString());
        }

        var arguments = new List<string>
        {
            "install",
            version,
            "--architecture",
            "x86_64",
            "--yes",
            "--accept-eula",
            "--non-interactive",
            "--no-banner",
            "--verbose",
            "--format",
            "ndjson"
        };
        if (!string.IsNullOrWhiteSpace(revision))
        {
            arguments.InsertRange(4, ["--changeset", revision]);
        }
        if (resumeInterruptedDownload)
        {
            arguments.Add("--resume");
            outputObserver?.Invoke(
                "Retrying with Unity CLI resume support so verified cached downloads are preserved.");
        }

        var selectedModuleIds = moduleIds
            .Where(id => !id.Equals("unity-editor", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedModuleIds.Length > 0)
        {
            arguments.Add("--module");
            arguments.AddRange(selectedModuleIds);
            if (selectedModuleIds.Contains("android", StringComparer.OrdinalIgnoreCase))
            {
                arguments.Add("--cm");
            }
        }

        UnityModuleInstallResult result = new(
            false,
            "Unity CLI could not install the selected Editor.",
            string.Empty);
        for (var attempt = 1; attempt <= MaximumInstallAttempts; attempt++)
        {
            outputObserver?.Invoke(
                $"Starting Unity CLI Editor install for {version} in {installRoot}"
                + (attempt == 1 ? "." : $" (attempt {attempt} of {MaximumInstallAttempts})."));
            result = await RunCliAsync(
                cliPath,
                arguments,
                progress,
                outputObserver,
                cancellationToken);
            if (result.Succeeded && IsNoPausedDownloadsResult(result.Output))
            {
                // --resume is a recovery command, not a general retry flag. If
                // Unity has no paused lifecycle to recover it exits successfully
                // without installing anything. Continue with a regular install,
                // which reuses verified files already present in Unity's cache.
                arguments.RemoveAll(argument =>
                    argument.Equals("--resume", StringComparison.OrdinalIgnoreCase));
                outputObserver?.Invoke(
                    "Unity CLI found no paused download. Continuing the requested installation from the verified cache.");
                progress?.Report(new UnityModuleInstallProgress(
                    "No paused download was found. Continuing the installation.",
                    Phase: "resolve",
                    ModuleName: "Unity Editor",
                    ModuleId: "unity-editor"));
                if (attempt == MaximumInstallAttempts)
                {
                    result = new(
                        false,
                        "Unity CLI found no paused download and did not perform the requested installation.",
                        result.Output);
                    break;
                }
                continue;
            }

            if (result.Succeeded || attempt == MaximumInstallAttempts)
            {
                break;
            }

            if (HasElevationCancellation(result.Output))
            {
                outputObserver?.Invoke("Windows administrator approval was canceled. Aborting installation.");
                result = new(false, "Windows administrator approval was canceled.", result.Output);
                break;
            }
            else if (IsTransientInstallerAccessFailure(result.Output))
            {
                outputObserver?.Invoke(
                    "Unity CLI could not access a cached installer. Waiting for the transient file lock to clear before retrying.");
                progress?.Report(new UnityModuleInstallProgress(
                    "Waiting for the cached installer file lock to clear.",
                    Phase: "verify",
                    ModuleName: "Unity Editor",
                    ModuleId: "unity-editor"));
            }
            else if (IsTransientNetworkFailure(result.Output))
            {
                // A normal install invocation safely reuses Unity's verified
                // cache. Do not switch to --resume here: that command only
                // resumes a persisted paused lifecycle and can otherwise be a
                // successful no-op.
                arguments.RemoveAll(argument =>
                    argument.Equals("--resume", StringComparison.OrdinalIgnoreCase));
                outputObserver?.Invoke(
                    $"The Editor download connection was interrupted. Retrying from cached data (attempt {attempt + 1} of {MaximumInstallAttempts}).");
                progress?.Report(new UnityModuleInstallProgress(
                    "Download interrupted. Retrying from verified cached data.",
                    Phase: "download",
                    ModuleName: "Unity Editor",
                    ModuleId: "unity-editor"));
            }
            else if (IsAntivirusOrDefenderBlock(result.Output))
            {
                // Antivirus or security software is actively blocking the
                // installer. Retrying will not help — the user must add an
                // exclusion. Break immediately with an actionable message.
                result = new(
                    false,
                    FormatAntivirusBlockMessage(installRoot),
                    result.Output);
                break;
            }
            else
            {
                break;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(TransientInstallerRetryDelay.TotalMilliseconds * attempt),
                cancellationToken);
        }

        if (!result.Succeeded)
        {
            return result;
        }

        var editorExecutablePath = Path.Combine(
            installRoot,
            version,
            "Editor",
            "Unity.exe");
        return File.Exists(editorExecutablePath)
            ? result
            : new(
                false,
                "Unity CLI exited successfully but the selected Editor was not installed.",
                result.Output);
    }

    public async Task<UnityModuleInstallResult> UninstallAsync(
        string version,
        Action<string>? outputObserver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new(false, "Choose an installed Unity Editor.", string.Empty);
        }

        var cliPath = await new UnityCliToolService()
            .GetVerifiedExecutablePathAsync(cancellationToken);
        if (cliPath is null)
        {
            return new(
                false,
                "Install the Unity CLI component before uninstalling an Editor.",
                string.Empty);
        }

        return await RunCliAsync(
            cliPath,
            [
                "uninstall",
                version,
                "--architecture",
                "x86_64",
                "--yes",
                "--non-interactive",
                "--no-banner",
                "--verbose",
                "--format",
                "ndjson"
            ],
            null,
            outputObserver,
            cancellationToken,
            initialMessage: $"Unity CLI is preparing to uninstall Unity {version}.",
            successMessage: $"Unity {version} was uninstalled.",
            timeoutMessage: "Unity CLI stopped responding while uninstalling the Editor.",
            fallbackErrorMessage: "Unity CLI could not uninstall the selected Editor.");
    }

    private static async Task<UnityModuleInstallResult> RunCliAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        IProgress<UnityModuleInstallProgress>? progress,
        Action<string>? outputObserver,
        CancellationToken cancellationToken,
        string initialMessage = "Unity CLI is resolving the Editor package.",
        string successMessage = "Unity Editor installed successfully.",
        string timeoutMessage = "Unity CLI stopped responding during the Editor installation.",
        string fallbackErrorMessage = "Unity CLI could not install the selected Editor.")
    {
        progress?.Report(new UnityModuleInstallProgress(
            initialMessage,
            Phase: "resolve",
            ModuleName: "Unity Editor",
            ModuleId: "unity-editor"));

        var output = new StringBuilder();
        void HandleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }
            outputObserver?.Invoke(line);
            var parsed = ParseProgress(line);
            if (parsed is not null)
            {
                progress?.Report(parsed);
            }
        }

        var runResult = await ElevatedUnityCliRunner.RunAsync(
            cliPath,
            arguments,
            HandleLine,
            InactivityTimeout,
            cancellationToken);

        if (runResult.TimedOut)
        {
            return new(false, timeoutMessage, output.ToString());
        }

        if (!string.IsNullOrWhiteSpace(runResult.StartError))
        {
            return new(false, runResult.StartError, output.ToString());
        }

        var text = output.ToString().Trim();
        return runResult.ExitCode == 0 && !HasStructuredFailure(text)
            ? new(true, successMessage, text)
            : new(false, FindErrorMessage(text, fallbackErrorMessage), text);
    }

    private static UnityModuleInstallProgress? ParseProgress(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;
            var message = ReadString(data, "msg")
                ?? ReadString(data, "message")
                ?? ReadString(root, "message");
            var phase = ReadString(data, "phase") ?? ReadString(data, "status") ?? "download";
            double? percentage = null;
            foreach (var name in new[] { "pct", "percentage", "percent", "progress" })
            {
                if (data.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out var parsed))
                {
                    percentage = parsed <= 1 && name == "progress" ? parsed * 100 : parsed;
                    break;
                }
            }

            if (message is null && percentage is null)
            {
                return null;
            }

            NormalizeProgressPhase(message, ref phase, ref percentage);
            var moduleName = ExtractModuleName(message);
            var isEditorProgress = moduleName?.StartsWith(
                "Unity (",
                StringComparison.OrdinalIgnoreCase) == true;
            return new(
                message ?? "Installing Unity Editor.",
                percentage,
                phase,
                isEditorProgress ? "Unity Editor" : moduleName,
                ModuleId: isEditorProgress ? "unity-editor" : null);
        }
        catch (JsonException)
        {
            var moduleName = ExtractModuleName(line);
            var isEditorProgress = moduleName?.StartsWith(
                "Unity (",
                StringComparison.OrdinalIgnoreCase) == true;
            return new(
                line,
                Phase: line.Contains("download", StringComparison.OrdinalIgnoreCase) ? "download" : "install",
                ModuleName: isEditorProgress ? "Unity Editor" : moduleName,
                ModuleId: isEditorProgress ? "unity-editor" : null);
        }
    }

    private static string? ExtractModuleName(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = ItemProgressMessagePattern.Match(message.Trim());
        return match.Success
            ? match.Groups["name"].Value.Trim().TrimEnd('.', '…')
            : null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void NormalizeProgressPhase(
        string? message,
        ref string phase,
        ref double? percentage)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (message.Contains("validat", StringComparison.OrdinalIgnoreCase)
            || message.Contains("verif", StringComparison.OrdinalIgnoreCase))
        {
            phase = "verify";
            // Unity CLI 1.0.0-beta.3 reports validation as 100% download
            // progress even though installation has not started. Validation
            // has no measurable duration, so expose it as indeterminate.
            percentage = null;
        }
        else if (message.Contains("starting install", StringComparison.OrdinalIgnoreCase))
        {
            phase = "install";
            percentage = null;
        }
    }

    private static bool IsTransientInstallerAccessFailure(string output)
        => output.Contains("EPERM: operation not permitted, open", StringComparison.OrdinalIgnoreCase)
           || output.Contains("EBUSY: resource busy or locked", StringComparison.OrdinalIgnoreCase)
           || output.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientNetworkFailure(string output)
        => output.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
           || output.Contains("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase)
           || output.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
           || output.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
           || output.Contains("request was aborted", StringComparison.OrdinalIgnoreCase)
           || output.Contains("timed out", StringComparison.OrdinalIgnoreCase)
           || output.Contains("timeout", StringComparison.OrdinalIgnoreCase)
           || output.Contains("HTTP 408", StringComparison.OrdinalIgnoreCase)
           || output.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)
           || output.Contains("HTTP 500", StringComparison.OrdinalIgnoreCase)
           || output.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase)
           || output.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase)
           || output.Contains("HTTP 504", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoPausedDownloadsResult(string output)
    {
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString()?.Equals("result", StringComparison.OrdinalIgnoreCase) == true
                    && root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Object
                    && ReadString(data, "reason")?.Equals(
                        "no_paused_downloads",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore non-JSON diagnostics surrounding the result envelope.
            }
        }

        return false;
    }

    private static bool HasElevationCancellation(string output)
        => output.Contains("\"ELEVATION_CANCELLED\"", StringComparison.OrdinalIgnoreCase)
           || output.Contains(
               "The Windows elevation prompt was cancelled or timed out.",
               StringComparison.OrdinalIgnoreCase);

    private static string? ValidateWritableLocation(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probePath = Path.Combine(path, $".fluenityhub-write-test-{Guid.NewGuid():N}.tmp");
            try
            {
                using var probe = new FileStream(
                    probePath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                probe.WriteByte(0);
                probe.Flush(flushToDisk: true);
            }
            finally
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static string FindErrorMessage(string output, string fallback)
    {
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryReadStructuredError(root, out var message))
                {
                    return message;
                }
            }
            catch (JsonException)
            {
                // Plain-text diagnostics are considered below.
            }
        }

        return output.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line =>
                    !line.StartsWith('{')
                    && (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            ?? fallback;
    }

    private static bool TryReadStructuredError(JsonElement root, out string message)
    {
        if (root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var code = ReadString(error, "code");
                var detail = ReadString(error, "message");
                if (code?.Equals("ELEVATION_CANCELLED", StringComparison.OrdinalIgnoreCase) == true)
                {
                    message = "Windows administrator approval was not completed. Retry the installation and accept the elevation prompt.";
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    message = FormatActionableInstallError(detail);
                    return true;
                }
            }
        }

        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("failures", out var failures)
            && failures.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in failures.EnumerateArray())
            {
                var reason = ReadString(failure, "reason");
                if (!string.IsNullOrWhiteSpace(reason))
                {
                    message = FormatActionableInstallError(reason);
                    return true;
                }
            }
        }

        message = string.Empty;
        return false;
    }

    private static string FormatActionableInstallError(string message)
    {
        if (message.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows administrator approval is required to install the selected modules. "
                + "Retry and approve the UAC prompt. Downloaded files remain cached.";
        }

        if (message.Contains("executing the installer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("INSTALL_ERROR", StringComparison.Ordinal))
        {
            return "The Unity Editor installer exited before completing. "
                + "The verified download was kept. Open the installation log for the Unity CLI details, then retry.";
        }

        return message;
    }

    private static bool IsAntivirusOrDefenderBlock(string output)
        => output.Contains("virus", StringComparison.OrdinalIgnoreCase)
           || output.Contains("threat", StringComparison.OrdinalIgnoreCase)
           || output.Contains("quarantine", StringComparison.OrdinalIgnoreCase)
           || output.Contains("blocked by policy", StringComparison.OrdinalIgnoreCase)
           || output.Contains("blocked by your organization", StringComparison.OrdinalIgnoreCase)
           || output.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
           || output.Contains("Operation did not complete successfully because the file contains a virus", StringComparison.OrdinalIgnoreCase);

    private static string FormatAntivirusBlockMessage(string installRoot)
        => $"The Unity installer was blocked by antivirus or security software. "
           + $"Add '{installRoot}' and Unity's download cache to your antivirus exclusions, then retry. "
           + "Downloaded files remain cached.";

    internal static string? GetEditorInstallStorageError(
        string installRoot,
        string downloadLocation)
        => null;

    private static string? CheckDiskSpace(string installRoot, string downloadLocation)
        => null;

    /// <summary>
    /// Deletes cached installer executables for the specified Unity version
    /// from the download location. Returns the number of files deleted.
    /// </summary>
    internal static IEnumerable<string> GetCandidateDownloadLocations(string? installRoot = null)
    {
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Configured Unity Hub download location
        try
        {
            var hubLocation = new UnityHubLocationSettingsService().GetDownloadLocation();
            if (!string.IsNullOrWhiteSpace(hubLocation))
            {
                locations.Add(hubLocation);
            }
        }
        catch { }

        // 2. Default Unity Hub download location (%APPDATA%\UnityHub\downloads)
        try
        {
            var defaultHubLocation = UnityHubLocationSettingsService.DefaultDownloadLocation;
            if (!string.IsNullOrWhiteSpace(defaultHubLocation))
            {
                locations.Add(defaultHubLocation);
            }
        }
        catch { }

        // 3. Downloads folder sibling/subfolder relative to installRoot (e.g. T:\unity\downloads if installRoot is T:\unity\editor)
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            try
            {
                var trimmed = Path.TrimEndingDirectorySeparator(installRoot);
                var parent = Path.GetDirectoryName(trimmed);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    locations.Add(Path.Combine(parent, "downloads"));
                }
                locations.Add(Path.Combine(trimmed, "downloads"));
            }
            catch { }
        }

        // 4. Local AppData Unity downloads folder (%LOCALAPPDATA%\Unity\downloads)
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                locations.Add(Path.Combine(localAppData, "Unity", "downloads"));
            }
        }
        catch { }

        return locations.Where(Directory.Exists);
    }

    /// <summary>
    /// Deletes cached installer executables for Unity versions that are no
    /// longer installed across all candidate download locations. Returns the total number of bytes reclaimed.
    /// </summary>
    public static long CleanStaleCachedInstallers(
        string? installRoot,
        IReadOnlyCollection<string> activeVersions,
        Action<string>? outputObserver = null)
    {
        var activeSet = new HashSet<string>(activeVersions, StringComparer.OrdinalIgnoreCase);
        long reclaimedBytes = 0;
        var locations = GetCandidateDownloadLocations(installRoot);

        foreach (var location in locations)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(location, "UnitySetup*"))
                {
                    var fileName = Path.GetFileName(file);
                    // Skip if any active version is mentioned in the filename.
                    if (activeSet.Any(version => fileName.Contains(version, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    try
                    {
                        var fileInfo = new FileInfo(file);
                        var fileSize = fileInfo.Length;
                        fileInfo.Delete();
                        reclaimedBytes += fileSize;
                        outputObserver?.Invoke($"Cleaned stale cached installer: {file} ({FormatBytes(fileSize)})");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        outputObserver?.Invoke($"Could not delete stale installer {file}: {ex.Message}");
                    }
                }

                // Also clean stale Android NDK zips that don't belong to any active version.
                foreach (var file in Directory.EnumerateFiles(location, "android-ndk-*"))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30))
                        {
                            var fileSize = fileInfo.Length;
                            fileInfo.Delete();
                            reclaimedBytes += fileSize;
                            outputObserver?.Invoke(
                                $"Cleaned stale Android NDK archive: {fileInfo.Name} ({FormatBytes(fileSize)})");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Best effort.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outputObserver?.Invoke($"Could not scan download cache '{location}' for cleanup: {ex.Message}");
            }
        }

        return reclaimedBytes;
    }

    private static bool HasStructuredFailure(string output)
    {
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString()?.Equals(
                        "result",
                        StringComparison.OrdinalIgnoreCase) == true
                    && root.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.False)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Plain-text diagnostics are handled by the exit code.
            }
        }

        return false;
    }

    private static void TryCancelRedirectedReads(Process process)
    {
        try
        {
            process.CancelOutputRead();
        }
        catch
        {
        }

        try
        {
            process.CancelErrorRead();
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }
}
