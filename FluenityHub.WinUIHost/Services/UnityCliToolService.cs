using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record UnityCliStatus(
    bool IsInstalled,
    string? Version,
    string? ExecutablePath);

public sealed record UnityCliReleaseInfo(
    string Version,
    Uri DownloadUri,
    string Sha256,
    long? DownloadSizeBytes);

public sealed record UnityCliDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double? Percentage);

public sealed class UnityCliToolService
{
    private const long MaximumBinaryBytes = 256L * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    public const string ReleaseChannelDisplayName = "Beta";
    private const string ReleaseChannel = "beta";
    private const string CdnBaseUrl = "https://public-cdn.cloud.unity3d.com/hub/prod/cli/";
    private const string ManifestUrl = CdnBaseUrl + "latest-" + ReleaseChannel + ".json";
    private const string TrustedHost = "public-cdn.cloud.unity3d.com";
    private static readonly Regex SafeVersionPattern =
        new("^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex SemanticVersionPattern = new(
        "^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z.-]+))?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern =
        new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ToolRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluenityHub",
        "Tools",
        "UnityCLI");

    private static string StateFilePath => Path.Combine(ToolRootPath, "current.json");

    public UnityCliStatus GetStatus()
    {
        try
        {
            var state = ReadState();
            if (state is not null && IsValidVersion(state.Version) && Sha256Pattern.IsMatch(state.Sha256))
            {
                var managedExecutablePath = GetVersionExecutablePath(state.Version);
                if (File.Exists(managedExecutablePath))
                {
                    return new UnityCliStatus(true, state.Version, managedExecutablePath);
                }
            }

            // Check official Unity installer location (%LOCALAPPDATA%\Unity\bin\unity.exe)
            var officialExecutablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unity",
                "bin",
                "unity.exe");
            if (File.Exists(officialExecutablePath))
            {
                var probedVersion = ProbeExecutableVersion(officialExecutablePath) ?? "1.0.0";
                return new UnityCliStatus(true, probedVersion, officialExecutablePath);
            }

            // Check system / user PATH
            var pathExecutable = FindExecutableInPath("unity.exe");
            if (!string.IsNullOrWhiteSpace(pathExecutable) && File.Exists(pathExecutable))
            {
                var probedVersion = ProbeExecutableVersion(pathExecutable) ?? "1.0.0";
                return new UnityCliStatus(true, probedVersion, pathExecutable);
            }

            return new UnityCliStatus(false, null, null);
        }
        catch
        {
            return new UnityCliStatus(false, null, null);
        }
    }

    public async Task<string?> GetVerifiedExecutablePathAsync(
        CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        if (!status.IsInstalled || string.IsNullOrWhiteSpace(status.ExecutablePath) || !File.Exists(status.ExecutablePath))
        {
            return null;
        }

        var state = ReadState();
        if (state is not null && string.Equals(GetVersionExecutablePath(state.Version), status.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            var actualHash = await ComputeSha256Async(status.ExecutablePath, cancellationToken);
            return actualHash.Equals(state.Sha256, StringComparison.OrdinalIgnoreCase)
                ? status.ExecutablePath
                : null;
        }

        return File.Exists(status.ExecutablePath) ? status.ExecutablePath : null;
    }

    private static string? ProbeExecutableVersion(string executablePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            if (!string.IsNullOrWhiteSpace(output) && SafeVersionPattern.IsMatch(output))
            {
                return output;
            }

            var fileVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion) && !fileVersion.Equals("0.0.0.0", StringComparison.Ordinal))
            {
                return fileVersion.Trim();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExecutableInPath(string executableName)
    {
        try
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathVariable)) return null;
            var directories = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in directories)
            {
                try
                {
                    var fullPath = Path.Combine(dir.Trim(), executableName);
                    if (File.Exists(fullPath)) return Path.GetFullPath(fullPath);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public async Task<UnityCliReleaseInfo> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        NetworkConnectivityService.Current.EnsureCanAttemptInternet();
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        manifestRequest.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true
        };
        using var manifestResponse = await HttpClient.SendAsync(
            manifestRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        ValidateTrustedResponse(manifestResponse);
        if (manifestResponse.Content.Headers.ContentLength is > MaximumManifestBytes)
        {
            throw new InvalidDataException("The Unity CLI release manifest is larger than expected.");
        }

        await using var responseStream =
            await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using var manifestStream = new MemoryStream();
        var manifestBuffer = new byte[16 * 1024];
        while (true)
        {
            var count = await responseStream.ReadAsync(manifestBuffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (manifestStream.Length + count > MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    "The Unity CLI release manifest is larger than expected.");
            }

            await manifestStream.WriteAsync(
                manifestBuffer.AsMemory(0, count),
                cancellationToken);
        }

        manifestStream.Position = 0;
        var manifest = await JsonSerializer.DeserializeAsync(
            manifestStream,
            RuntimeJsonContext.Default.UnityCliManifest,
            cancellationToken)
            ?? throw new InvalidDataException("Unity returned an empty CLI release manifest.");

        if (!IsValidVersion(manifest.Version))
        {
            throw new InvalidDataException("Unity returned an invalid CLI version.");
        }

        var platformKey = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win32-x64",
            Architecture.Arm64 => "win32-arm64",
            _ => throw new PlatformNotSupportedException(
                "Unity CLI requires a 64-bit x64 or ARM64 version of Windows.")
        };

        if (!manifest.Binaries.TryGetValue(platformKey, out var binary)
            || string.IsNullOrWhiteSpace(binary.Filename)
            || !Sha256Pattern.IsMatch(binary.Sha256))
        {
            throw new InvalidDataException(
                $"Unity did not publish a valid CLI binary for {platformKey}.");
        }

        var safeFilename = Path.GetFileName(binary.Filename);
        if (!safeFilename.Equals(binary.Filename, StringComparison.Ordinal)
            || !safeFilename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unity returned an invalid CLI filename.");
        }

        var downloadUri = new Uri(
            $"https://{TrustedHost}/hub/prod/cli/{manifest.Version}/{safeFilename}");
        long? downloadSize = null;
        using (var request = new HttpRequestMessage(HttpMethod.Head, downloadUri))
        using (var response = await HttpClient.SendAsync(
                   request,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            ValidateTrustedResponse(response);
            downloadSize = response.Content.Headers.ContentLength;
            if (downloadSize is > MaximumBinaryBytes)
            {
                throw new InvalidDataException("The Unity CLI download is larger than expected.");
            }
        }

        return new UnityCliReleaseInfo(
            manifest.Version,
            downloadUri,
            binary.Sha256.ToLowerInvariant(),
            downloadSize);
    }

    public static bool IsReleaseNewer(string? installedVersion, string releaseVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return true;
        }

        if (installedVersion.Equals(releaseVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryCompareSemanticVersions(releaseVersion, installedVersion, out var comparison)
            ? comparison > 0
            : true;
    }

    private static bool TryCompareSemanticVersions(
        string left,
        string right,
        out int comparison)
    {
        var leftMatch = SemanticVersionPattern.Match(left);
        var rightMatch = SemanticVersionPattern.Match(right);
        if (!leftMatch.Success || !rightMatch.Success)
        {
            comparison = 0;
            return false;
        }

        foreach (var component in new[] { "major", "minor", "patch" })
        {
            comparison = CompareNumericIdentifiers(
                leftMatch.Groups[component].Value,
                rightMatch.Groups[component].Value);
            if (comparison != 0)
            {
                return true;
            }
        }

        var leftPrerelease = leftMatch.Groups["prerelease"].Value;
        var rightPrerelease = rightMatch.Groups["prerelease"].Value;
        if (leftPrerelease.Length == 0 || rightPrerelease.Length == 0)
        {
            comparison = leftPrerelease.Length == rightPrerelease.Length
                ? 0
                : leftPrerelease.Length == 0
                    ? 1
                    : -1;
            return true;
        }

        var leftIdentifiers = leftPrerelease.Split('.');
        var rightIdentifiers = rightPrerelease.Split('.');
        var identifierCount = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
        for (var index = 0; index < identifierCount; index++)
        {
            var leftIdentifier = leftIdentifiers[index];
            var rightIdentifier = rightIdentifiers[index];
            var leftIsNumeric = leftIdentifier.All(char.IsDigit);
            var rightIsNumeric = rightIdentifier.All(char.IsDigit);
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = CompareNumericIdentifiers(leftIdentifier, rightIdentifier);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                comparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(leftIdentifier, rightIdentifier);
            }

            if (comparison != 0)
            {
                return true;
            }
        }

        comparison = leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
        return true;
    }

    private static int CompareNumericIdentifiers(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        left = left.Length == 0 ? "0" : left;
        right = right.Length == 0 ? "0" : right;

        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.CompareOrdinal(left, right);
    }

    public async Task<UnityCliStatus> InstallAsync(
        UnityCliReleaseInfo release,
        IProgress<UnityCliDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        NetworkConnectivityService.Current.EnsureCanAttemptInternet();
        ValidateRelease(release);
        Directory.CreateDirectory(ToolRootPath);

        var versionDirectory = GetVersionDirectory(release.Version);
        Directory.CreateDirectory(versionDirectory);
        var destinationPath = GetVersionExecutablePath(release.Version);

        if (File.Exists(destinationPath))
        {
            var existingHash = await ComputeSha256Async(destinationPath, cancellationToken);
            if (existingHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await ValidateCompatibilityAsync(destinationPath, cancellationToken);
                WriteState(new UnityCliState(release.Version, release.Sha256));
                DeleteOldVersions(release.Version);
                return new UnityCliStatus(true, release.Version, destinationPath);
            }
        }

        var temporaryPath = Path.Combine(
            ToolRootPath,
            $".unity-{Guid.NewGuid():N}.download");
        try
        {
            using var response = await HttpClient.GetAsync(
                release.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateTrustedResponse(response);

            var totalBytes = response.Content.Headers.ContentLength ?? release.DownloadSizeBytes;
            if (totalBytes is > MaximumBinaryBytes)
            {
                throw new InvalidDataException("The Unity CLI download is larger than expected.");
            }

            string actualHash;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                long bytesReceived = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }

                    bytesReceived += count;
                    if (bytesReceived > MaximumBinaryBytes)
                    {
                        throw new InvalidDataException("The Unity CLI download is larger than expected.");
                    }

                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report(new UnityCliDownloadProgress(
                        bytesReceived,
                        totalBytes,
                        totalBytes is > 0 ? bytesReceived * 100d / totalBytes.Value : null));
                }

                await destination.FlushAsync(cancellationToken);
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Unity CLI verification failed because the SHA-256 checksum did not match.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            try
            {
                await ValidateCompatibilityAsync(destinationPath, cancellationToken);
            }
            catch
            {
                TryDeleteFile(destinationPath);
                throw;
            }

            WriteState(new UnityCliState(release.Version, release.Sha256));
            DeleteOldVersions(release.Version);
            return new UnityCliStatus(true, release.Version, destinationPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            TryDeleteEmptyDirectory(versionDirectory);
        }
    }

    public void Remove()
    {
        var root = Path.GetFullPath(ToolRootPath);
        var expectedParent = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluenityHub",
            "Tools"));
        if (!IsDirectChild(root, expectedParent)
            || !Path.GetFileName(root).Equals("UnityCLI", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Unity CLI storage path could not be verified.");
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task ValidateCompatibilityAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("install-modules");
        startInfo.ArgumentList.Add("--help");

        using var process = new Process { StartInfo = startInfo };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if (!process.Start())
            {
                throw new InvalidDataException("The downloaded Unity CLI could not be started.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = $"{await standardOutput}\n{await standardError}";
            if (process.ExitCode != 0
                || !output.Contains("--editor-version", StringComparison.OrdinalIgnoreCase)
                || !output.Contains("--module", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "This Unity CLI release is not compatible with FluenityHub module installation.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            throw new TimeoutException("Unity CLI compatibility validation timed out.");
        }
        catch
        {
            TryTerminate(process);
            throw;
        }
    }

    private static UnityCliState? ReadState()
    {
        if (!File.Exists(StateFilePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize(
            File.ReadAllText(StateFilePath),
            RuntimeJsonContext.Default.UnityCliState);
    }

    private static void WriteState(UnityCliState state)
    {
        Directory.CreateDirectory(ToolRootPath);
        var temporaryStatePath = Path.Combine(
            ToolRootPath,
            $".current-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                temporaryStatePath,
                JsonSerializer.Serialize(state, RuntimeJsonContext.Default.UnityCliState));
            File.Move(temporaryStatePath, StateFilePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryStatePath);
        }
    }

    private static string GetVersionDirectory(string version)
    {
        if (!IsValidVersion(version))
        {
            throw new InvalidDataException("The Unity CLI version is invalid.");
        }

        var path = Path.GetFullPath(Path.Combine(ToolRootPath, version));
        if (!IsDirectChild(path, Path.GetFullPath(ToolRootPath)))
        {
            throw new InvalidDataException("The Unity CLI version path is invalid.");
        }

        return path;
    }

    private static string GetVersionExecutablePath(string version)
        => Path.Combine(GetVersionDirectory(version), "unity.exe");

    private static bool IsValidVersion(string? version)
        => !string.IsNullOrWhiteSpace(version) && SafeVersionPattern.IsMatch(version);

    private static bool IsDirectChild(string path, string parent)
        => Path.GetDirectoryName(path)?.TrimEnd(Path.DirectorySeparatorChar)
            .Equals(
                parent.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) == true;

    private static void ValidateRelease(UnityCliReleaseInfo release)
    {
        if (!IsValidVersion(release.Version)
            || !Sha256Pattern.IsMatch(release.Sha256)
            || !release.DownloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !release.DownloadUri.Host.Equals(TrustedHost, StringComparison.OrdinalIgnoreCase)
            || release.DownloadSizeBytes is > MaximumBinaryBytes)
        {
            throw new InvalidDataException("The Unity CLI release metadata is invalid.");
        }
    }

    private static void ValidateTrustedResponse(HttpResponseMessage response)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null
            || !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !finalUri.Host.Equals(TrustedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unity CLI download redirected to an untrusted address.");
        }
    }

    private static void DeleteOldVersions(string currentVersion)
    {
        if (!Directory.Exists(ToolRootPath))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(ToolRootPath))
        {
            if (Path.GetFileName(directory).Equals(currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(directory);
                if (IsDirectChild(fullPath, Path.GetFullPath(ToolRootPath)))
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            catch
            {
                // A previous version in use can be cleaned up during a later update.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of an incomplete temporary file.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of an empty version directory.
        }
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
            // Best-effort cleanup of a failed compatibility check.
        }
    }

    internal sealed record UnityCliState(string Version, string Sha256);

    internal sealed class UnityCliManifest
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("binaries")]
        public Dictionary<string, UnityCliBinary> Binaries { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class UnityCliBinary
    {
        [JsonPropertyName("filename")]
        public string Filename { get; init; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;
    }
}
