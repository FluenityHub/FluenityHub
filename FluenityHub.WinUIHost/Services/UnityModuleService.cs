using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record UnityModuleCatalog(
    string ProductName,
    IReadOnlyList<UnityEditorModuleInfo> Modules);

public sealed record UnityModuleInstallResult(
    bool Succeeded,
    string Message,
    string Output);

public sealed record UnityModuleInstallProgress(
    string Message,
    double? Percentage = null,
    string? Phase = null,
    string? ModuleName = null,
    long? BytesReceived = null,
    long? TotalBytes = null,
    string? ModuleId = null);

public sealed class UnityModuleService
{
    private const int MaximumLicenseDocumentBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CliInactivityTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CliStreamDrainTimeout = TimeSpan.FromSeconds(2);
    private const string UnityPathToken = "{UNITY_PATH}";
    private static readonly IReadOnlyDictionary<string, string> PlaybackEngineFolders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ios"] = "iOSSupport",
            ["android"] = "AndroidPlayer",
            ["appletv"] = "AppleTVSupport",
            ["visionos"] = "VisionOSPlayer",
            ["webgl"] = "WebGLSupport",
            ["universal-windows-platform"] = "MetroSupport",
            ["lumin"] = "LuminSupport"
        };
    private static readonly Regex StandaloneModulePattern = new(
        "^(?<platform>windows|linux|mac)-(?<variant>il2cpp|mono|server)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string MissingLicensingLoggingAdapterMessage =
        "Licensing SDK logging callback is not registered";
    private static readonly Regex DependencyMessagePattern = new(
        "^Adding module (?<module>.+?) as dependency of (?<parent>.+?)\\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HttpClient LicenseHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public UnityModuleCatalog LoadCatalog(UnityEditorInfo editor)
    {
        var manifestPath = Path.Combine(editor.InstallDirectory, "modules.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "This Editor does not include Unity Hub module metadata. Only Editors installed through Unity Hub can add modules.");
        }

        var productName = LoadProductName(editor.InstallDirectory) ?? $"Unity {editor.Version}";
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Unity module manifest has an unexpected format.");
        }

        var modules = new List<UnityEditorModuleInfo>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            ParseModule(element, string.Empty, modules);
        }

        var installationMetadata = ReadRemovalMetadata(document.RootElement);
        ReconcileInstalledModuleState(editor.InstallDirectory, modules, installationMetadata);

        foreach (var module in modules)
        {
            module.CanRemove = module.IsInstalled
                && !module.IsRequired
                && HasRemovablePaths(module, modules);
        }

        return new UnityModuleCatalog(productName, modules);
    }

    public long GetAvailableDiskSpace(string installDirectory)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(installDirectory));
        return string.IsNullOrWhiteSpace(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
    }

    public async Task<string?> LoadLicenseContentAsync(
        UnityLicenseTerm term,
        CancellationToken cancellationToken = default)
    {
        if (!term.ModuleId.Equals("android-sdk-ndk-tools", StringComparison.OrdinalIgnoreCase)
            || term.NavigateUri is null)
        {
            return null;
        }

        if (!term.NavigateUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The license document must use a secure HTTPS address.");
        }

        using var response = await LicenseHttpClient.GetAsync(
            term.NavigateUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumLicenseDocumentBytes)
        {
            throw new InvalidDataException("The license document is larger than expected.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var bufferedStream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (bufferedStream.Length + bytesRead > MaximumLicenseDocumentBytes)
            {
                throw new InvalidDataException("The license document is larger than expected.");
            }

            await bufferedStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        bufferedStream.Position = 0;
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumLicenseDocumentBytes
        };

        using var reader = XmlReader.Create(bufferedStream, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        var license = document
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName.Equals("license", StringComparison.OrdinalIgnoreCase)
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase)
                    && attribute.Value.Equals("android-sdk-license", StringComparison.OrdinalIgnoreCase)));
        var content = WebUtility.HtmlDecode(license?.Value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("The Android SDK license text was not found in the document.");
        }

        return content;
    }

    public Task<UnityModuleInstallResult> InstallAsync(
        string version,
        string installDirectory,
        IReadOnlyCollection<string> moduleIds,
        IProgress<UnityModuleInstallProgress>? progress = null,
        Action<string>? outputObserver = null,
        CancellationToken cancellationToken = default)
        => InstallModulesAsync(
            version,
            installDirectory,
            moduleIds,
            reinstall: false,
            progress,
            outputObserver,
            cancellationToken);

    public Task<UnityModuleInstallResult> RepairAsync(
        string version,
        string installDirectory,
        IReadOnlyCollection<string> moduleIds,
        IProgress<UnityModuleInstallProgress>? progress = null,
        Action<string>? outputObserver = null,
        CancellationToken cancellationToken = default)
        => InstallModulesAsync(
            version,
            installDirectory,
            moduleIds,
            reinstall: true,
            progress,
            outputObserver,
            cancellationToken);

    private async Task<UnityModuleInstallResult> InstallModulesAsync(
        string version,
        string installDirectory,
        IReadOnlyCollection<string> moduleIds,
        bool reinstall,
        IProgress<UnityModuleInstallProgress>? progress,
        Action<string>? outputObserver,
        CancellationToken cancellationToken)
    {
        if (moduleIds.Count == 0)
        {
            return new UnityModuleInstallResult(false, "Select at least one module.", string.Empty);
        }

        var unityCliPath = await new UnityCliToolService()
            .GetVerifiedExecutablePathAsync(cancellationToken);
        if (unityCliPath is null)
        {
            return new UnityModuleInstallResult(
                false,
                "Install the Unity CLI component before installing modules.",
                string.Empty);
        }

        if (string.IsNullOrWhiteSpace(installDirectory)
            || !File.Exists(Path.Combine(installDirectory, "Editor", "Unity.exe")))
        {
            return new UnityModuleInstallResult(
                false,
                "The Unity Editor installation directory is invalid.",
                installDirectory);
        }

        var installLocationError = ValidateWritableLocation(installDirectory);
        if (installLocationError is not null)
        {
            return new UnityModuleInstallResult(
                false,
                $"FluenityHub cannot add modules to '{installDirectory}'. "
                + "Use a writable Unity Editor location so background installs do not require administrator approval. "
                + installLocationError,
                installLocationError);
        }

        progress?.Report(new UnityModuleInstallProgress(
            "Checking whether Unity CLI recognizes the installed editor.",
            Phase: "resolve"));
        var registrationResult = await EnsureEditorRegisteredAsync(
            unityCliPath,
            version,
            Path.Combine(installDirectory, "Editor", "Unity.exe"),
            outputObserver,
            cancellationToken);
        if (!registrationResult.Succeeded)
        {
            return registrationResult;
        }

        var installPlan = BuildModuleInstallPlan(installDirectory, moduleIds);
        foreach (var skippedModuleId in installPlan.AlreadyInstalledModuleIds)
        {
            outputObserver?.Invoke(
                $"Skipping module {skippedModuleId} because its installed payload was verified on disk.");
        }

        if (installPlan.ModuleIds.Count == 0)
        {
            return new UnityModuleInstallResult(
                true,
                "The selected modules are already installed.",
                string.Empty);
        }

        var repairsStaleManifestState = installPlan.StaleManifestModuleIds.Count > 0;
        if (repairsStaleManifestState)
        {
            outputObserver?.Invoke(
                "Unity CLI metadata marks the following missing module payloads as installed: "
                + string.Join(", ", installPlan.StaleManifestModuleIds)
                + ". Reinstalling only those requested modules to repair Unity's installation state.");
        }

        progress?.Report(new UnityModuleInstallProgress(
            reinstall
                ? "Editor registered. Resolving the selected module for repair."
                : repairsStaleManifestState
                    ? "Repairing stale Unity module state before installation."
                : "Editor registered. Resolving selected modules and dependencies.",
            Phase: "resolve"));

        var cliArguments = new List<string>
        {
            "install-modules",
            "--editor-version",
            version,
            "--module"
        };

        foreach (var moduleId in installPlan.ModuleIds)
        {
            cliArguments.Add(moduleId);
        }

        if (repairsStaleManifestState && !reinstall)
        {
            // Force Unity CLI to reconsider modules whose manifest state disagrees with disk.
            // Hidden child dependencies are expanded explicitly in BuildModuleInstallPlan.
            cliArguments.Add("--force");
        }
        else if (reinstall)
        {
            cliArguments.Add("--reinstall");
        }

        if (reinstall)
        {
            cliArguments.Add("--no-childModules");
        }

        if (!reinstall
            && !repairsStaleManifestState
            && installPlan.ModuleIds.Contains("android", StringComparer.OrdinalIgnoreCase))
        {
            cliArguments.Add("--cm");
        }
        cliArguments.AddRange([
            "--accept-eula",
            "--yes",
            "--non-interactive",
            "--no-banner",
            "--verbose",
            "--retries",
            "3",
            "--format",
            "ndjson"
        ]);

        var output = new StringBuilder();
        var actionableOutput = new StringBuilder();

        void HandleOutputLine(string line)
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

            if (!IsIgnorableCliDiagnostic(line))
            {
                lock (actionableOutput)
                {
                    actionableOutput.AppendLine(line);
                }

                var progressUpdate = ParseProgress(line);
                if (progressUpdate is not null)
                {
                    progress?.Report(progressUpdate);
                }
            }
        }

        try
        {
            progress?.Report(new UnityModuleInstallProgress(
                "Waiting for Windows administrator approval before downloading modules.",
                Phase: "resolve"));
            var elevatedResult = await ElevatedUnityCliRunner.RunAsync(
                unityCliPath,
                cliArguments,
                HandleOutputLine,
                CliInactivityTimeout,
                cancellationToken);
            if (elevatedResult.TimedOut)
            {
                return new UnityModuleInstallResult(
                    false,
                    "Unity CLI stopped responding for five minutes. Retry the installation.",
                    Snapshot(output));
            }

            if (!string.IsNullOrWhiteSpace(elevatedResult.StartError))
            {
                return new UnityModuleInstallResult(
                    false,
                    elevatedResult.StartError,
                    Snapshot(output));
            }

            var capturedActionableOutput = Snapshot(actionableOutput);
            var cliFailed = elevatedResult.ExitCode != 0
                || HasStructuredFailure(capturedActionableOutput);
            var recoveredModules = await TryRecoverCachedAndroidNdkAsync(
                installDirectory,
                installPlan.ModuleIds,
                progress,
                HandleOutputLine,
                cancellationToken);

            var missingVerifiedPayloads = FindMissingVerifiedPayloads(
                installDirectory,
                installPlan.ModuleIds);
            var capturedOutput = Snapshot(output);
            if (missingVerifiedPayloads.Count > 0)
            {
                var missingModules = string.Join(", ", missingVerifiedPayloads);
                outputObserver?.Invoke(
                    "Unity CLI reported success, but payload verification failed for: "
                    + missingModules
                    + ".");
                return new UnityModuleInstallResult(
                    false,
                    "Unity CLI did not install every requested module payload. Missing: "
                    + missingModules
                    + ". Retry keeps the verified modules and repairs only the missing items.",
                    capturedOutput);
            }

            if (cliFailed && recoveredModules.Count == 0)
            {
                return new UnityModuleInstallResult(
                    false,
                    string.IsNullOrWhiteSpace(capturedActionableOutput)
                        ? $"Unity CLI exited with code {elevatedResult.ExitCode}."
                        : GetLastOutputLine(capturedActionableOutput),
                    capturedOutput);
            }

            return new UnityModuleInstallResult(
                true,
                reinstall
                    ? "The selected module was repaired."
                    : "The selected modules were installed.",
                capturedOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UnityModuleInstallResult(
                false,
                $"Module {(reinstall ? "repair" : "installation")} failed: {ex.Message}",
                Snapshot(output));
        }
    }

    public async Task<UnityModuleInstallResult> RemoveAsync(
        string installDirectory,
        string moduleId,
        IProgress<UnityModuleInstallProgress>? progress = null,
        Action<string>? outputObserver = null,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(installDirectory, "modules.json");
        if (!File.Exists(manifestPath))
        {
            return new(false, "This Editor does not include Unity Hub module metadata.", string.Empty);
        }

        try
        {
            progress?.Report(new("Reading installed module metadata.", Phase: "resolve", ModuleId: moduleId));
            var locationError = ValidateWritableLocation(installDirectory);
            if (locationError is not null)
            {
                return new(
                    false,
                    $"FluenityHub cannot remove modules from '{installDirectory}'. Use a writable Editor location. {locationError}",
                    locationError);
            }

            using (new FileStream(
                       manifestPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.Read))
            {
            }

            var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            using var document = JsonDocument.Parse(manifestText);
            var modules = ReadRemovalMetadata(document.RootElement);
            var module = modules.FirstOrDefault(candidate =>
                candidate.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase));
            if (module is null || !module.IsInstalled)
            {
                return new(false, $"Module {moduleId} is not installed.", string.Empty);
            }

            if (module.IsRequired)
            {
                return new(false, $"{module.Name} is required by its parent module and cannot be removed separately.", string.Empty);
            }

            var siblingModules = modules
                .Where(candidate => !candidate.Id.Equals(moduleId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!HasRemovablePaths(module, siblingModules))
            {
                return new(false, $"Unity manages no removable files for {module.Name} inside this Editor.", string.Empty);
            }

            var groupMemberIds = siblingModules
                .Where(candidate =>
                    candidate.SyncId.Equals(moduleId, StringComparison.OrdinalIgnoreCase)
                    || candidate.ParentId.Equals(moduleId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removalPaths = ResolveRemovalPaths(installDirectory, module, siblingModules);
            var otherTargets = siblingModules
                .Where(candidate => !groupMemberIds.Contains(candidate.Id))
                .Select(candidate => (candidate.Id, Target: ResolveRemovalTarget(installDirectory, candidate)))
                .Where(candidate => candidate.Target is not null)
                .Select(candidate => (candidate.Id, Target: candidate.Target!))
                .ToArray();
            var playbackEngineModule = IsPlaybackEngineModule(module.Id);

            foreach (var target in removalPaths)
            {
                EnsureStrictlyInsideEditor(installDirectory, target, module.Name);
                EnsureNoEscapingReparsePoint(installDirectory, target, module.Name);
                if (!playbackEngineModule && otherTargets.Any(other => IsSameOrUnder(other.Target, target)))
                {
                    return new(false, $"{module.Name} shares its install location with another installed module and cannot be removed safely.", target);
                }
            }

            var removedDescendantIds = new HashSet<string>(groupMemberIds, StringComparer.OrdinalIgnoreCase);
            foreach (var other in otherTargets)
            {
                if (removalPaths.Any(removed => IsStrictlyUnder(other.Target, removed)))
                {
                    removedDescendantIds.Add(other.Id);
                }
            }

            for (var index = 0; index < removalPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = removalPaths[index];
                var percentage = removalPaths.Count == 0
                    ? 80
                    : 10 + (70d * index / removalPaths.Count);
                progress?.Report(new(
                    $"Removing {Path.GetFileName(target)}.",
                    percentage,
                    "remove",
                    module.Name,
                    ModuleId: module.Id));
                outputObserver?.Invoke($"Removing owned module path: {target}");
                DeleteOwnedPath(target);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Updating installed module metadata.", 85, "remove", module.Name, ModuleId: module.Id));
            removedDescendantIds.Add(module.Id);
            await UpdateInstalledStateAsync(manifestPath, removedDescendantIds, cancellationToken);
            progress?.Report(new($"Removed {module.Name}.", 100, "removed", module.Name, ModuleId: module.Id));
            return new(true, $"Removed {module.Name} from this Editor.", string.Join(Environment.NewLine, removalPaths));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, $"Module removal failed: {ex.Message}", ex.ToString());
        }
    }

    private static string? ValidateWritableLocation(string directory)
    {
        try
        {
            var probePath = Path.Combine(
                directory,
                $".fluenityhub-write-test-{Guid.NewGuid():N}.tmp");
            using (new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1,
                       FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return ex.Message;
        }
    }

    private enum EditorRegistrationStatus
    {
        Registered,
        Missing,
        Unknown
    }

    private static async Task<UnityModuleInstallResult> EnsureEditorRegisteredAsync(
        string unityCliPath,
        string editorVersion,
        string editorExecutablePath,
        Action<string>? outputObserver,
        CancellationToken cancellationToken)
    {
        var status = await GetEditorRegistrationStatusAsync(
            unityCliPath,
            editorVersion,
            editorExecutablePath,
            outputObserver,
            cancellationToken);
        if (status == EditorRegistrationStatus.Registered)
        {
            outputObserver?.Invoke(
                $"Unity CLI already recognizes Unity {editorVersion}; registration was skipped.");
            return new UnityModuleInstallResult(
                true,
                "The installed editor is registered.",
                string.Empty);
        }

        if (status == EditorRegistrationStatus.Unknown)
        {
            outputObserver?.Invoke(
                "Unity CLI editor discovery did not complete; continuing with module resolution.");
            return new UnityModuleInstallResult(
                true,
                "Module resolution will verify the installed editor.",
                string.Empty);
        }

        return await RegisterEditorAsync(
            unityCliPath,
            editorExecutablePath,
            outputObserver,
            cancellationToken);
    }

    private static async Task<EditorRegistrationStatus> GetEditorRegistrationStatusAsync(
        string unityCliPath,
        string editorVersion,
        string editorExecutablePath,
        Action<string>? outputObserver,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = unityCliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add("editors");
        startInfo.ArgumentList.Add("--installed");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("ndjson");
        startInfo.ArgumentList.Add("--no-banner");
        startInfo.ArgumentList.Add("--non-interactive");

        using var process = new Process { StartInfo = startInfo };
        using var discoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            discoveryTimeout.Token);
        try
        {
            if (!process.Start())
            {
                return EditorRegistrationStatus.Unknown;
            }

            process.StandardInput.Close();
            var standardOutputTask =
                process.StandardOutput.ReadToEndAsync(discoveryCancellation.Token);
            var standardErrorTask =
                process.StandardError.ReadToEndAsync(discoveryCancellation.Token);
            await process.WaitForExitAsync(discoveryCancellation.Token);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;
            var output = string.Join(
                Environment.NewLine,
                new[] { standardOutput, standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            foreach (var line in output.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                outputObserver?.Invoke(line);
            }

            if (process.ExitCode != 0)
            {
                return EditorRegistrationStatus.Unknown;
            }

            return ContainsRegisteredEditor(
                    standardOutput,
                    editorVersion,
                    editorExecutablePath)
                ? EditorRegistrationStatus.Registered
                : EditorRegistrationStatus.Missing;
        }
        catch (OperationCanceledException)
            when (discoveryTimeout.IsCancellationRequested
                  && !cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            outputObserver?.Invoke(
                "Unity CLI editor discovery timed out after 30 seconds.");
            return EditorRegistrationStatus.Unknown;
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception ex)
        {
            TryTerminate(process);
            outputObserver?.Invoke(
                $"Unity CLI editor discovery could not be completed: {ex.Message}");
            return EditorRegistrationStatus.Unknown;
        }
    }

    private static bool ContainsRegisteredEditor(
        string output,
        string editorVersion,
        string editorExecutablePath)
    {
        var expectedPath = Path.GetFullPath(editorExecutablePath);
        foreach (var line in output.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var version = ReadString(document.RootElement, "version");
                var location = ReadString(document.RootElement, "location");
                if (version?.Equals(editorVersion, StringComparison.OrdinalIgnoreCase) == true
                    && !string.IsNullOrWhiteSpace(location)
                    && Path.GetFullPath(location).Equals(
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore non-structured diagnostic lines around the NDJSON records.
            }
            catch (Exception) when (
                line.StartsWith('{')
                && line.EndsWith('}'))
            {
                // An invalid path in one record must not invalidate other editor records.
            }
        }

        return false;
    }

    private static async Task<UnityModuleInstallResult> RegisterEditorAsync(
        string unityCliPath,
        string editorExecutablePath,
        Action<string>? outputObserver,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = unityCliPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("editors");
        startInfo.ArgumentList.Add("add");
        startInfo.ArgumentList.Add(editorExecutablePath);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("ndjson");
        startInfo.ArgumentList.Add("--no-banner");
        startInfo.ArgumentList.Add("--non-interactive");
        startInfo.ArgumentList.Add("--verbose");

        using var process = new Process { StartInfo = startInfo };
        using var registrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var registrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            registrationTimeout.Token);
        try
        {
            if (!process.Start())
            {
                return new UnityModuleInstallResult(
                    false,
                    "Unity CLI could not register the installed editor.",
                    string.Empty);
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(registrationCancellation.Token);
            var standardOutput = await standardOutputTask.WaitAsync(registrationCancellation.Token);
            var standardError = await standardErrorTask.WaitAsync(registrationCancellation.Token);
            var output = string.Join(
                Environment.NewLine,
                new[] { standardOutput, standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            foreach (var line in output.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                outputObserver?.Invoke(line);
            }

            var isAlreadyRegistered = output.Contains(
                "already registered",
                StringComparison.OrdinalIgnoreCase);
            return process.ExitCode == 0 || isAlreadyRegistered
                ? new UnityModuleInstallResult(true, "The installed editor is registered.", output)
                : new UnityModuleInstallResult(
                    false,
                    string.IsNullOrWhiteSpace(output)
                        ? $"Unity CLI could not register the editor (exit code {process.ExitCode})."
                        : GetLastOutputLine(output),
                    output);
        }
        catch (OperationCanceledException)
            when (registrationTimeout.IsCancellationRequested
                  && !cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            outputObserver?.Invoke(
                "Unity CLI editor registration timed out after 30 seconds; module resolution will verify the editor.");
            return new UnityModuleInstallResult(
                true,
                "Module resolution will verify the installed editor.",
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception ex)
        {
            TryTerminate(process);
            return new UnityModuleInstallResult(
                false,
                $"Unity CLI could not register the editor: {ex.Message}",
                ex.ToString());
        }
    }

    private static bool IsIgnorableCliDiagnostic(string line)
        => line.Contains(
            MissingLicensingLoggingAdapterMessage,
            StringComparison.OrdinalIgnoreCase);

    private static string? LoadProductName(string installDirectory)
    {
        var metadataPath = Path.Combine(installDirectory, "metadata.hub.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return ReadString(document.RootElement, "productName");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeCategory(string category)
        => category.Equals("Language packs (Preview)", StringComparison.OrdinalIgnoreCase)
            ? "Language packs (preview)"
            : category;

    private sealed record ModuleRemovalMetadata(
        string Id,
        string Name,
        string ParentId,
        string SyncId,
        string Destination,
        string RenameTo,
        string RenameFrom,
        string DownloadUrl,
        long DownloadSizeBytes,
        long InstalledSizeBytes,
        bool IsInstalled,
        bool IsRequired);

    private sealed record ModuleInstallPlan(
        IReadOnlyList<string> ModuleIds,
        IReadOnlyList<string> AlreadyInstalledModuleIds,
        IReadOnlyList<string> StaleManifestModuleIds);

    private static ModuleInstallPlan BuildModuleInstallPlan(
        string installDirectory,
        IReadOnlyCollection<string> requestedModuleIds)
    {
        var requestedIds = requestedModuleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifestPath = Path.Combine(installDirectory, "modules.json");
        if (!File.Exists(manifestPath))
        {
            return new ModuleInstallPlan(requestedIds, [], []);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var metadata = ReadRemovalMetadata(document.RootElement);
            var metadataById = metadata
                .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var modulesToInstall = new List<string>();
            var alreadyInstalled = new List<string>();
            var staleManifest = new List<string>();

            foreach (var requestedId in requestedIds)
            {
                if (!metadataById.TryGetValue(requestedId, out var module))
                {
                    modulesToInstall.Add(requestedId);
                    continue;
                }

                if (TryGetInstalledPayloadState(
                        installDirectory,
                        module,
                        metadata,
                        out var payloadIsPresent)
                    && payloadIsPresent)
                {
                    alreadyInstalled.Add(requestedId);
                    continue;
                }

                if (!module.IsInstalled)
                {
                    modulesToInstall.Add(requestedId);
                    continue;
                }

                if (!TryGetInstalledPayloadState(
                        installDirectory,
                        module,
                        metadata,
                        out payloadIsPresent))
                {
                    alreadyInstalled.Add(requestedId);
                    continue;
                }

                modulesToInstall.Add(requestedId);
                staleManifest.Add(requestedId);
            }

            var dependencyRoots = requestedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dependencyAdded = true;
            while (dependencyAdded)
            {
                dependencyAdded = false;
                foreach (var dependency in metadata)
                {
                    if ((!dependencyRoots.Contains(dependency.ParentId)
                         && !dependencyRoots.Contains(dependency.SyncId))
                        || dependencyRoots.Contains(dependency.Id))
                    {
                        continue;
                    }

                    dependencyRoots.Add(dependency.Id);
                    dependencyAdded = true;
                    if (TryGetInstalledPayloadState(
                            installDirectory,
                            dependency,
                            metadata,
                            out var dependencyPayloadIsPresent)
                        && dependencyPayloadIsPresent)
                    {
                        continue;
                    }

                    if (dependency.IsInstalled
                        && !TryGetInstalledPayloadState(
                            installDirectory,
                            dependency,
                            metadata,
                            out dependencyPayloadIsPresent))
                    {
                        continue;
                    }

                    modulesToInstall.Add(dependency.Id);
                    if (dependency.IsInstalled)
                    {
                        staleManifest.Add(dependency.Id);
                    }
                }
            }

            return new ModuleInstallPlan(
                modulesToInstall.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                alreadyInstalled,
                staleManifest.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException)
        {
            // Unity CLI remains the source of truth if its manifest cannot be inspected safely.
            return new ModuleInstallPlan(requestedIds, [], []);
        }
    }

    private static bool HasInstalledPayload(
        string installDirectory,
        ModuleRemovalMetadata module,
        IReadOnlyCollection<ModuleRemovalMetadata> modules)
        => !TryGetInstalledPayloadState(installDirectory, module, modules, out var isPresent)
            || isPresent;

    private static bool TryGetInstalledPayloadState(
        string installDirectory,
        ModuleRemovalMetadata module,
        IReadOnlyCollection<ModuleRemovalMetadata> modules,
        out bool isPresent)
    {
        if (IsPlaybackEngineModule(module.Id))
        {
            var playbackPaths = ResolveRemovalPaths(installDirectory, module, modules).ToArray();
            isPresent = playbackPaths.Any(PathContainsPayload);
            return playbackPaths.Length > 0;
        }

        var ownedModules = new[] { module }
            .Concat(modules.Where(candidate =>
                candidate.ParentId.Equals(module.Id, StringComparison.OrdinalIgnoreCase)
                || candidate.SyncId.Equals(module.Id, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var ownedTargets = ownedModules
            .Select(candidate => ResolveRemovalTarget(installDirectory, candidate))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ownedTargets.Length == 0)
        {
            isPresent = false;
            return false;
        }

        isPresent = ownedTargets.All(PathContainsPayload);
        return true;
    }

    private static IReadOnlyList<string> FindMissingVerifiedPayloads(
        string installDirectory,
        IReadOnlyCollection<string> requestedModuleIds)
    {
        var requestedIds = requestedModuleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var manifestPath = Path.Combine(installDirectory, "modules.json");
        if (!File.Exists(manifestPath))
        {
            return requestedIds;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var metadata = ReadRemovalMetadata(document.RootElement);
            var metadataById = metadata
                .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            return requestedIds
                .Where(id => !metadataById.TryGetValue(id, out var module)
                    || (TryGetInstalledPayloadState(
                            installDirectory,
                            module,
                            metadata,
                            out var payloadIsPresent)
                        && !payloadIsPresent))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidDataException)
        {
            return requestedIds;
        }
    }

    private static bool PathContainsPayload(string path)
    {
        if (File.Exists(path))
        {
            return true;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> TryRecoverCachedAndroidNdkAsync(
        string installDirectory,
        IReadOnlyCollection<string> requestedModuleIds,
        IProgress<UnityModuleInstallProgress>? progress,
        Action<string> outputObserver,
        CancellationToken cancellationToken)
    {
        const string supportedModuleId = "android-ndk-r27c";
        if (!requestedModuleIds.Contains(supportedModuleId, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var manifestPath = Path.Combine(installDirectory, "modules.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        ModuleRemovalMetadata? module;
        using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            module = ReadRemovalMetadata(document.RootElement).FirstOrDefault(candidate =>
                candidate.Id.Equals(supportedModuleId, StringComparison.OrdinalIgnoreCase));
        }

        if (module is null || HasInstalledPayload(installDirectory, module, [module]))
        {
            return [];
        }

        if (!Uri.TryCreate(module.DownloadUrl, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps
            || !downloadUri.Host.Equals("dl.google.com", StringComparison.OrdinalIgnoreCase)
            || !downloadUri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || module.DownloadSizeBytes <= 0
            || module.InstalledSizeBytes <= 0)
        {
            return [];
        }

        var archiveName = Uri.UnescapeDataString(Path.GetFileName(downloadUri.AbsolutePath));
        if (!Path.GetFileName(archiveName).Equals(archiveName, StringComparison.Ordinal))
        {
            return [];
        }

        var downloadRoot = Path.GetFullPath(new UnityHubLocationSettingsService().GetDownloadLocation());
        var archivePath = Path.GetFullPath(Path.Combine(downloadRoot, archiveName));
        if (!Path.GetDirectoryName(archivePath)!.Equals(
                Path.TrimEndingDirectorySeparator(downloadRoot),
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(archivePath)
            || new FileInfo(archivePath).Length != module.DownloadSizeBytes)
        {
            return [];
        }

        var destination = ResolveUnityPath(installDirectory, module.Destination);
        var renameFrom = ResolveUnityPath(installDirectory, module.RenameFrom);
        var renameTo = ResolveUnityPath(installDirectory, module.RenameTo);
        if (destination is null
            || renameFrom is null
            || renameTo is null
            || !IsSameOrUnder(renameFrom, destination))
        {
            return [];
        }

        EnsureStrictlyInsideEditor(installDirectory, destination, module.Name);
        EnsureStrictlyInsideEditor(installDirectory, renameFrom, module.Name);
        EnsureStrictlyInsideEditor(installDirectory, renameTo, module.Name);
        var sourceRelativePath = Path.GetRelativePath(destination, renameFrom);
        if (Path.IsPathRooted(sourceRelativePath)
            || sourceRelativePath.Equals("..", StringComparison.Ordinal)
            || sourceRelativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return [];
        }

        var stagingParent = Path.GetDirectoryName(renameTo)
            ?? throw new InvalidDataException("The Android NDK target directory is invalid.");
        var stagingDirectory = Path.Combine(
            stagingParent,
            $".fluenity-{supportedModuleId}-{Guid.NewGuid():N}");
        EnsureStrictlyInsideEditor(installDirectory, stagingDirectory, module.Name);

        progress?.Report(new UnityModuleInstallProgress(
            "Repairing the cached Android NDK package.",
            Phase: "install",
            ModuleName: module.Name,
            ModuleId: module.Id));
        outputObserver(
            "Unity CLI could not finalize the cached Android NDK archive. "
            + "FluenityHub is applying the verified manifest rename safely.");

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await ExtractVerifiedZipAsync(
                archivePath,
                stagingDirectory,
                checked(module.InstalledSizeBytes + Math.Max(64L * 1024 * 1024, module.InstalledSizeBytes / 4)),
                cancellationToken);
            var stagedSource = Path.GetFullPath(Path.Combine(stagingDirectory, sourceRelativePath));
            if (!IsStrictlyUnder(stagedSource, stagingDirectory) || !PathContainsPayload(stagedSource))
            {
                throw new InvalidDataException("The cached Android NDK archive has an unexpected layout.");
            }

            if (Directory.Exists(renameTo))
            {
                if (PathContainsPayload(renameTo))
                {
                    throw new IOException("The Android NDK target already contains files; it was not overwritten.");
                }

                Directory.Delete(renameTo, recursive: true);
            }

            Directory.Move(stagedSource, renameTo);
            if (!PathContainsPayload(renameTo))
            {
                throw new InvalidDataException("Android NDK payload verification failed after extraction.");
            }

            outputObserver("Recovered Android NDK from Unity CLI's verified cached archive.");
            return [module.Id];
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch
            {
                // A locked staging directory is harmless and remains scoped inside this Editor.
            }
        }
    }

    private static string? ResolveUnityPath(string installDirectory, string templatePath)
        => string.IsNullOrWhiteSpace(templatePath)
            || (!templatePath.StartsWith($"{UnityPathToken}/", StringComparison.OrdinalIgnoreCase)
                && !templatePath.StartsWith($"{UnityPathToken}\\", StringComparison.OrdinalIgnoreCase))
                ? null
                : Path.GetFullPath(templatePath.Replace(
                    UnityPathToken,
                    Path.TrimEndingDirectorySeparator(installDirectory),
                    StringComparison.OrdinalIgnoreCase));

    private static async Task ExtractVerifiedZipAsync(
        string archivePath,
        string stagingDirectory,
        long maximumExpandedBytes,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > 500_000)
        {
            throw new InvalidDataException("The cached Android NDK archive has an invalid entry count.");
        }

        long expandedBytes = 0;
        var normalizedStagingRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stagingDirectory)) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(entry.FullName)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("The cached Android NDK archive contains an unsafe entry.");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > maximumExpandedBytes)
            {
                throw new InvalidDataException("The cached Android NDK archive expands beyond its expected size.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
            if (!targetPath.StartsWith(normalizedStagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The cached Android NDK archive contains a path traversal entry.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var source = entry.Open();
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, 128 * 1024, cancellationToken);
        }
    }

    private static List<ModuleRemovalMetadata> ReadRemovalMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The Unity module manifest has an unexpected format.");
        }

        var modules = new List<ModuleRemovalMetadata>();
        foreach (var element in root.EnumerateArray())
        {
            ReadRemovalMetadata(element, string.Empty, modules);
        }

        return modules;
    }

    private static void ReadRemovalMetadata(
        JsonElement element,
        string inheritedParentId,
        ICollection<ModuleRemovalMetadata> modules)
    {
        var id = ReadString(element, "id")?.Trim();
        if (!string.IsNullOrWhiteSpace(id))
        {
            modules.Add(new(
                id,
                ReadString(element, "name")?.Trim() ?? id,
                ReadString(element, "parent")?.Trim() ?? inheritedParentId,
                ReadString(element, "sync")?.Trim() ?? string.Empty,
                ReadString(element, "destination")?.Trim() ?? string.Empty,
                ReadString(element, "renameTo")?.Trim() ?? string.Empty,
                ReadString(element, "renameFrom")?.Trim() ?? string.Empty,
                ReadString(element, "downloadUrl")?.Trim() ?? string.Empty,
                ReadSize(element, "downloadSize"),
                ReadSize(element, "installedSize"),
                ReadBoolean(element, "selected") == true || ReadBoolean(element, "isInstalled") == true,
                ReadBoolean(element, "required") == true));
        }

        if (element.TryGetProperty("subModules", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                ReadRemovalMetadata(child, id ?? inheritedParentId, modules);
            }
        }
    }

    private static bool HasRemovablePaths(
        UnityEditorModuleInfo module,
        IReadOnlyCollection<UnityEditorModuleInfo> siblings)
        => IsPlaybackEngineModule(module.Id)
            || IsEditorInternalTemplatePath(ResolveTemplateRemovalTarget(
                module.Id,
                module.Destination,
                module.RenameTo))
            || siblings.Any(sibling =>
                (sibling.SyncId.Equals(module.Id, StringComparison.OrdinalIgnoreCase)
                 || sibling.ParentId.Equals(module.Id, StringComparison.OrdinalIgnoreCase))
                && IsEditorInternalTemplatePath(ResolveTemplateRemovalTarget(
                    sibling.Id,
                    sibling.Destination,
                    sibling.RenameTo)));

    private static bool HasRemovablePaths(
        ModuleRemovalMetadata module,
        IReadOnlyCollection<ModuleRemovalMetadata> siblings)
        => IsPlaybackEngineModule(module.Id)
            || IsEditorInternalTemplatePath(ResolveTemplateRemovalTarget(
                module.Id,
                module.Destination,
                module.RenameTo))
            || siblings.Any(sibling =>
                (sibling.SyncId.Equals(module.Id, StringComparison.OrdinalIgnoreCase)
                 || sibling.ParentId.Equals(module.Id, StringComparison.OrdinalIgnoreCase))
                && IsEditorInternalTemplatePath(ResolveTemplateRemovalTarget(
                    sibling.Id,
                    sibling.Destination,
                    sibling.RenameTo)));

    private static bool IsPlaybackEngineModule(string moduleId)
        => PlaybackEngineFolders.ContainsKey(moduleId)
            || StandaloneModulePattern.IsMatch(moduleId);

    private static void ReconcileInstalledModuleState(
        string installDirectory,
        IReadOnlyCollection<UnityEditorModuleInfo> modules,
        IReadOnlyCollection<ModuleRemovalMetadata> installationMetadata)
    {
        var modulesById = modules.ToDictionary(module => module.Id, StringComparer.OrdinalIgnoreCase);
        var metadataById = installationMetadata
            .GroupBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (metadataById.TryGetValue(module.Id, out var metadata)
                && TryGetInstalledPayloadState(
                    installDirectory,
                    metadata,
                    installationMetadata,
                    out var payloadIsPresent))
            {
                module.IsInstalled = payloadIsPresent;
            }

            if (!module.IsInstalled)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(module.ParentId)
                && modulesById.TryGetValue(module.ParentId, out var parent)
                && !parent.IsInstalled)
            {
                module.IsInstalled = false;
                continue;
            }

            if (metadataById.TryGetValue(module.Id, out metadata)
                && metadata.IsInstalled
                && !HasInstalledPayload(installDirectory, metadata, installationMetadata))
            {
                module.IsInstalled = false;
                continue;
            }

            var payloadState = GetInstalledPayloadState(installDirectory, module);
            if (payloadState == InstalledPayloadState.Missing)
            {
                module.IsInstalled = false;
            }
        }
    }

    private static InstalledPayloadState GetInstalledPayloadState(
        string installDirectory,
        UnityEditorModuleInfo module)
    {
        var playbackRoot = Path.Combine(installDirectory, "Editor", "Data", "PlaybackEngines");
        if (PlaybackEngineFolders.TryGetValue(module.Id, out var supportFolder))
        {
            return Directory.Exists(Path.Combine(playbackRoot, supportFolder))
                ? InstalledPayloadState.Present
                : InstalledPayloadState.Missing;
        }

        var standalone = StandaloneModulePattern.Match(module.Id);
        if (standalone.Success)
        {
            var supportDirectory = Path.Combine(
                playbackRoot,
                $"{standalone.Groups["platform"].Value}StandaloneSupport");
            var variationsDirectory = Path.Combine(supportDirectory, "Variations");
            if (!Directory.Exists(variationsDirectory))
            {
                return InstalledPayloadState.Missing;
            }

            var variant = standalone.Groups["variant"].Value;
            return Directory.EnumerateDirectories(variationsDirectory).Any(path =>
            {
                var name = Path.GetFileName(path);
                return variant.Equals("server", StringComparison.OrdinalIgnoreCase)
                    ? name.Contains("server", StringComparison.OrdinalIgnoreCase)
                    : name.Contains(variant, StringComparison.OrdinalIgnoreCase)
                      && !name.Contains("server", StringComparison.OrdinalIgnoreCase);
            })
                ? InstalledPayloadState.Present
                : InstalledPayloadState.Missing;
        }

        var templatePath = ResolveTemplateRemovalTarget(
            module.Id,
            module.Destination,
            module.RenameTo);
        if (!IsEditorInternalTemplatePath(templatePath))
        {
            return InstalledPayloadState.Unknown;
        }

        var payloadPath = Path.GetFullPath(templatePath!.Replace(
            UnityPathToken,
            Path.TrimEndingDirectorySeparator(installDirectory),
            StringComparison.OrdinalIgnoreCase));
        return PathContainsPayload(payloadPath)
            ? InstalledPayloadState.Present
            : InstalledPayloadState.Missing;
    }

    private enum InstalledPayloadState
    {
        Unknown,
        Missing,
        Present
    }

    private static string? ResolveTemplateRemovalTarget(
        string moduleId,
        string destination,
        string renameTo)
    {
        if (!string.IsNullOrWhiteSpace(renameTo))
        {
            return renameTo;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        if (moduleId.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(destination, $"{moduleId[9..]}.po");
        }

        if (moduleId.Equals("documentation", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(Path.TrimEndingDirectorySeparator(destination))
                .Equals("Documentation", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(destination, "Documentation");
        }

        return destination;
    }

    private static bool IsEditorInternalTemplatePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.StartsWith($"{UnityPathToken}/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith($"{UnityPathToken}\\", StringComparison.OrdinalIgnoreCase));

    private static List<string> ResolveRemovalPaths(
        string installDirectory,
        ModuleRemovalMetadata module,
        IReadOnlyCollection<ModuleRemovalMetadata> siblings)
    {
        var playbackRoot = Path.Combine(installDirectory, "Editor", "Data", "PlaybackEngines");
        if (PlaybackEngineFolders.TryGetValue(module.Id, out var supportFolder))
        {
            return Directory.Exists(playbackRoot)
                ? Directory.EnumerateDirectories(playbackRoot)
                    .Where(path => Path.GetFileName(path).Equals(supportFolder, StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : [];
        }

        var standalone = StandaloneModulePattern.Match(module.Id);
        if (standalone.Success)
        {
            var supportName = $"{standalone.Groups["platform"].Value}StandaloneSupport";
            var variant = standalone.Groups["variant"].Value;
            var supportDirectories = Directory.Exists(playbackRoot)
                ? Directory.EnumerateDirectories(playbackRoot)
                    .Where(path => Path.GetFileName(path).Equals(supportName, StringComparison.OrdinalIgnoreCase))
                : [];
            var result = new List<string>();
            foreach (var supportDirectory in supportDirectories)
            {
                var variationsDirectory = Path.Combine(supportDirectory, "Variations");
                if (!Directory.Exists(variationsDirectory))
                {
                    continue;
                }

                result.AddRange(Directory.EnumerateDirectories(variationsDirectory).Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return variant.Equals("server", StringComparison.OrdinalIgnoreCase)
                        ? name.Contains("server", StringComparison.OrdinalIgnoreCase)
                        : name.Contains(variant, StringComparison.OrdinalIgnoreCase)
                          && !name.Contains("server", StringComparison.OrdinalIgnoreCase);
                }));
            }

            return result;
        }

        var syncedChildren = siblings.Where(sibling =>
            sibling.SyncId.Equals(module.Id, StringComparison.OrdinalIgnoreCase)
            || sibling.ParentId.Equals(module.Id, StringComparison.OrdinalIgnoreCase));
        var ownedPaths = new[] { module }
            .Concat(syncedChildren)
            .Select(candidate => ResolveRemovalTarget(installDirectory, candidate))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ownedPaths.Count == 0)
        {
            return [];
        }

        return ownedPaths
            .Where(candidate => !ownedPaths.Any(other =>
                !other.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                && IsStrictlyUnder(candidate, other)))
            .ToList();
    }

    private static string? ResolveRemovalTarget(
        string installDirectory,
        ModuleRemovalMetadata module)
    {
        var templatePath = ResolveTemplateRemovalTarget(
            module.Id,
            module.Destination,
            module.RenameTo);
        return string.IsNullOrWhiteSpace(templatePath)
            ? null
            : Path.GetFullPath(templatePath.Replace(
                UnityPathToken,
                Path.TrimEndingDirectorySeparator(installDirectory),
                StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureStrictlyInsideEditor(
        string installDirectory,
        string target,
        string moduleName)
    {
        var editorRoot = Path.GetFullPath(installDirectory);
        var fullTarget = Path.GetFullPath(target);
        if (!IsStrictlyUnder(fullTarget, editorRoot))
        {
            throw new InvalidDataException(
                $"Refusing to remove {moduleName}: '{fullTarget}' is outside the Editor installation.");
        }
    }

    private static bool IsStrictlyUnder(string child, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(child));
        return !relative.Equals(".", StringComparison.Ordinal)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool IsSameOrUnder(string child, string parent)
        => Path.GetFullPath(child).Equals(Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase)
            || IsStrictlyUnder(child, parent);

    private static void EnsureNoEscapingReparsePoint(
        string installDirectory,
        string target,
        string moduleName)
    {
        var editorRoot = Path.GetFullPath(installDirectory);
        var current = Directory.GetParent(Path.GetFullPath(target));
        while (current is not null
               && !current.FullName.Equals(editorRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists
                && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    $"Refusing to remove {moduleName}: '{current.FullName}' redirects outside the verified module path.");
            }

            current = current.Parent;
        }
    }

    private static void DeleteOwnedPath(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        var directory = new DirectoryInfo(path);
        directory.Delete(recursive: directory.LinkTarget is null);
    }

    private static async Task UpdateInstalledStateAsync(
        string manifestPath,
        IReadOnlySet<string> removedModuleIds,
        CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken))
            ?? throw new InvalidDataException("The Unity module manifest is empty.");
        UpdateInstalledState(root, removedModuleIds);

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            $".{Path.GetFileName(manifestPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void UpdateInstalledState(JsonNode node, IReadOnlySet<string> removedModuleIds)
    {
        if (node is JsonObject item
            && item["id"]?.GetValue<string>() is { } id
            && removedModuleIds.Contains(id))
        {
            item["selected"] = false;
            item["isInstalled"] = false;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null)
                {
                    UpdateInstalledState(child, removedModuleIds);
                }
            }
        }
        else if (node is JsonObject container)
        {
            foreach (var child in container.Select(property => property.Value))
            {
                if (child is not null)
                {
                    UpdateInstalledState(child, removedModuleIds);
                }
            }
        }
    }

    private static void ParseModule(
        JsonElement element,
        string parentId,
        ICollection<UnityEditorModuleInfo> modules)
    {
        if (ReadBoolean(element, "hidden") == true || ReadBoolean(element, "visible") == false)
        {
            return;
        }

        var id = ReadString(element, "id");
        var name = ReadString(element, "name");
        var category = ReadString(element, "category");
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        modules.Add(new UnityEditorModuleInfo
        {
            Id = id,
            Name = name,
            Category = NormalizeCategory(category),
            ParentId = ReadString(element, "parent") ?? parentId,
            DownloadSizeBytes = ReadSize(element, "downloadSize"),
            InstalledSizeBytes = ReadSize(element, "installedSize"),
            IsInstalled = ReadBoolean(element, "selected") == true
                || ReadBoolean(element, "isInstalled") == true,
            IsRequired = ReadBoolean(element, "required") == true,
            Destination = ReadString(element, "destination") ?? string.Empty,
            RenameTo = ReadString(element, "renameTo") ?? string.Empty,
            SyncId = ReadString(element, "sync") ?? string.Empty,
            LicenseTerms = ReadLicenseTerms(element, id, name)
        });

        if (element.TryGetProperty("subModules", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                ParseModule(child, id, modules);
            }
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;


    private static bool? ReadBoolean(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;

    private static IReadOnlyList<UnityLicenseTerm> ReadLicenseTerms(
        JsonElement module,
        string moduleId,
        string moduleName)
    {
        if (!module.TryGetProperty("eula", out var eula)
            || eula.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var terms = new List<UnityLicenseTerm>();
        foreach (var entry in eula.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var label = ReadString(entry, "label")?.Trim();
            var message = ReadString(entry, "message")?.Trim() ?? string.Empty;
            var url = ReadString(entry, "url")?.Trim();
            Uri? navigateUri = null;
            if (!string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
                && parsedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                navigateUri = parsedUri;
            }

            if (string.IsNullOrWhiteSpace(label) && navigateUri is null && string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            terms.Add(new UnityLicenseTerm
            {
                ModuleId = moduleId,
                ModuleName = moduleName,
                Label = string.IsNullOrWhiteSpace(label) ? $"{moduleName} license terms" : label,
                Message = message,
                NavigateUri = navigateUri
            });
        }

        return terms;
    }

    private static long ReadSize(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var directValue))
        {
            return directValue;
        }

        return property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("value", out var value)
            && value.TryGetInt64(out var nestedValue)
                ? nestedValue
                : 0;
    }

    private static string GetLastOutputLine(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Reverse())
        {
            var wasStructured = false;
            try
            {
                using var document = JsonDocument.Parse(line);
                wasStructured = true;
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var root = document.RootElement;
                    if (TryReadStructuredError(root, out var structuredError))
                    {
                        return structuredError;
                    }

                    var error = ReadString(root, "error");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return error;
                    }

                    var message = ReadString(root, "message");
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
            }
            catch (JsonException)
            {
                // Plain-text CLI output is still useful as the failure message.
            }

            if (!wasStructured && !string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return "Unity CLI could not install the selected modules.";
    }

    private static bool HasStructuredFailure(string output)
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
                    && root.TryGetProperty("success", out var success)
                    && success.ValueKind == JsonValueKind.False)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Plain-text diagnostics are handled through the exit code.
            }
        }

        return false;
    }

    private static bool TryReadStructuredError(JsonElement root, out string message)
    {
        if (root.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var detail = ReadString(error, "message");
                if (!string.IsNullOrWhiteSpace(detail)
                    && !detail.Equals("Parent editor install failed", StringComparison.OrdinalIgnoreCase))
                {
                    message = FormatActionableInstallError(detail);
                    return true;
                }
            }

            foreach (var error in errors.EnumerateArray())
            {
                var detail = ReadString(error, "message");
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
                if (!string.IsNullOrWhiteSpace(reason)
                    && !reason.Equals("Parent editor install failed", StringComparison.OrdinalIgnoreCase))
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
        => message.Contains("requires elevation", StringComparison.OrdinalIgnoreCase)
            ? "Windows administrator approval is required to install this module. Retry and approve the UAC prompt. Downloaded files remain cached."
            : message;

    private static UnityModuleInstallProgress? ParseProgress(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return null;
            }

            var dependencyMatch = DependencyMessagePattern.Match(trimmed);
            return dependencyMatch.Success
                ? new UnityModuleInstallProgress(
                    trimmed,
                    Phase: "resolve",
                    ModuleName: dependencyMatch.Groups["module"].Value,
                    ModuleId: dependencyMatch.Groups["module"].Value)
                : new UnityModuleInstallProgress(trimmed);
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            var message = FindString(root, "message", "msg", "status", "detail") ?? string.Empty;
            var phase = FindString(root, "phase");
            var moduleName = FindString(root, "moduleName")
                ?? FindString(root, "module")
                ?? FindString(root, "name");
            var moduleId = FindString(root, "moduleId", "moduleID", "id");
            var percentage = FindNumber(
                root,
                "percentage",
                "percent",
                "percentComplete",
                "pct");
            if (percentage is null)
            {
                var progressValue = FindNumber(root, "progress");
                if (progressValue is not null)
                {
                    percentage = progressValue is >= 0 and <= 1
                        ? progressValue * 100
                        : progressValue;
                }
            }

            var bytesReceived = FindInt64(
                root,
                "bytesReceived",
                "downloadedBytes",
                "completedBytes");
            var totalBytes = FindInt64(root, "totalBytes", "bytesTotal");
            if (percentage is null && bytesReceived is >= 0 && totalBytes is > 0)
            {
                percentage = bytesReceived.Value * 100d / totalBytes.Value;
            }

            if (percentage is not null)
            {
                percentage = double.IsFinite(percentage.Value)
                    ? Math.Clamp(percentage.Value, 0, 100)
                    : null;
            }

            if (string.IsNullOrWhiteSpace(message)
                && percentage is null
                && string.IsNullOrWhiteSpace(phase))
            {
                return null;
            }

            return new UnityModuleInstallProgress(
                message.Trim(),
                percentage,
                phase?.Trim(),
                moduleName?.Trim(),
                bytesReceived,
                totalBytes,
                moduleId?.Trim());
        }
        catch (JsonException)
        {
            return new UnityModuleInstallProgress(trimmed);
        }
    }

    private static string? FindString(JsonElement element, params string[] propertyNames)
    {
        if (TryFindProperty(element, propertyNames, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static double? FindNumber(JsonElement element, params string[] propertyNames)
    {
        if (!TryFindProperty(element, propertyNames, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number)
                ? number
                : null;
    }

    private static long? FindInt64(JsonElement element, params string[] propertyNames)
    {
        var number = FindNumber(element, propertyNames);
        return number is >= 0 and <= long.MaxValue
            ? (long)number.Value
            : null;
    }

    private static bool TryFindProperty(
        JsonElement element,
        IReadOnlyCollection<string> propertyNames,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if ((property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    && TryFindProperty(property.Value, propertyNames, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, propertyNames, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void TryTerminate(Process process)
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
            // Best-effort cancellation only.
        }
    }

    private static void TryCancelRedirectedReads(Process process)
    {
        try
        {
            process.CancelOutputRead();
        }
        catch (InvalidOperationException)
        {
            // The output reader already completed.
        }

        try
        {
            process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
            // The error reader already completed.
        }
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString().Trim();
        }
    }
}
