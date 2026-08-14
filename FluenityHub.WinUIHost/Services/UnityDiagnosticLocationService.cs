using System.Text.RegularExpressions;

namespace FluenityHub_WinUIHost.Services;

public sealed record UnityDiagnosticLocation(
    string Key,
    string Path,
    string Source,
    bool Exists);

/// <summary>
/// Resolves diagnostic and cache locations shared by FluenityHub, Unity Hub,
/// and Unity Editor. Unity Hub data is anchored to the same roaming directory
/// as the shared Hub preferences, and Unity-supported cache overrides take
/// precedence over the documented Windows defaults.
/// </summary>
public sealed class UnityDiagnosticLocationService
{
    private static readonly string LocalAppData = Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData);

    private static readonly string RoamingAppData = Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData);

    private static readonly string UserProfile = Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile);

    private static readonly string CommonApplicationData = Environment.GetFolderPath(
        Environment.SpecialFolder.CommonApplicationData);

    public string UnityHubDataDirectory { get; } = ResolveUnityHubDataDirectory();

    public string GetUnityHubDataFilePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("A Unity Hub data file name is required.", nameof(fileName));
        }

        return Path.Combine(UnityHubDataDirectory, fileName);
    }

    public UnityDiagnosticLocation? Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return key switch
        {
            "FluenityHubInstallationLogs" => Create(
                key,
                Path.Combine(LocalAppData, "FluenityHub", "Logs", "ModuleInstallations"),
                "FluenityHub application data"),
            "UnityHubLogs" => Create(
                key,
                Path.Combine(UnityHubDataDirectory, "logs"),
                "Unity Hub shared data"),
            "AssetPackages" => ResolveAssetStoreCache(key),
            "CrashDumps" => Create(
                key,
                Path.Combine(LocalAppData, "CrashDumps"),
                "Windows Error Reporting"),
            "CrashReports" => Create(
                key,
                Path.GetTempPath(),
                "Unity crash-report root"),
            "EditorLogs" => Create(
                key,
                Path.Combine(LocalAppData, "Unity", "Editor"),
                "Unity documented default"),
            "LicensingLogs" => Create(
                key,
                Path.Combine(LocalAppData, "Unity"),
                "Unity documented default"),
            "GiCache" => Create(
                key,
                Path.Combine(LocalAppData, "Unity", "Caches", "GiCache"),
                "Unity documented default"),
            "PlayerLogs" => Create(
                key,
                Path.Combine(UserProfile, "AppData", "LocalLow"),
                "Unity documented Player-log root"),
            "UnityCache" => ResolveUnityPackageCache(key),
            _ => null
        };
    }

    public IReadOnlyList<UnityDiagnosticLocation> ResolveAll()
    {
        string[] keys =
        [
            "FluenityHubInstallationLogs",
            "UnityHubLogs",
            "AssetPackages",
            "CrashDumps",
            "CrashReports",
            "EditorLogs",
            "LicensingLogs",
            "GiCache",
            "PlayerLogs",
            "UnityCache"
        ];

        return keys
            .Select(Resolve)
            .OfType<UnityDiagnosticLocation>()
            .ToArray();
    }

    private static string ResolveUnityHubDataDirectory()
    {
        return Path.GetDirectoryName(UnityHubLocationSettingsService.InstallLocationFilePath)
            ?? Path.Combine(RoamingAppData, "UnityHub");
    }

    private static UnityDiagnosticLocation ResolveAssetStoreCache(string key)
    {
        var overriddenPath = ReadEnvironmentPath(
            "ASSETSTORE_CACHE_PATH",
            requireDirectory: false);
        return overriddenPath is not null
            ? Create(key, overriddenPath, "ASSETSTORE_CACHE_PATH")
            : Create(
                key,
                Path.Combine(RoamingAppData, "Unity", "Asset Store-5.x"),
                "Unity documented default");
    }

    private static UnityDiagnosticLocation ResolveUnityPackageCache(string key)
    {
        var environmentPath = ReadEnvironmentPath(
            "UPM_CACHE_ROOT",
            requireDirectory: false);
        if (environmentPath is not null)
        {
            return Create(key, environmentPath, "UPM_CACHE_ROOT");
        }

        foreach (var configurationPath in GetUpmConfigurationPaths())
        {
            var configuredPath = TryReadTomlPath(configurationPath, "cacheRoot");
            if (configuredPath is not null)
            {
                return Create(key, configuredPath, configurationPath);
            }
        }

        return Create(
            key,
            Path.Combine(LocalAppData, "Unity", "cache"),
            "Unity documented default");
    }

    private static IEnumerable<string> GetUpmConfigurationPaths()
    {
        var userOverride = ReadEnvironmentPath("UPM_USER_CONFIG_FILE", requireDirectory: false);
        yield return userOverride ?? Path.Combine(UserProfile, ".upmconfig.toml");

        var globalOverride = ReadEnvironmentPath("UPM_GLOBAL_CONFIG_FILE", requireDirectory: false);
        yield return globalOverride
            ?? Path.Combine(CommonApplicationData, "Unity", "config", "upmconfig.toml");
    }

    private static string? TryReadTomlPath(string configurationPath, string propertyName)
    {
        try
        {
            if (!File.Exists(configurationPath))
            {
                return null;
            }

            var pattern = $"^\\s*{Regex.Escape(propertyName)}\\s*=\\s*[\\\"'](?<path>.*?)[\\\"']\\s*(?:#.*)?$";
            foreach (var line in File.ReadLines(configurationPath))
            {
                var match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var value = match.Groups["path"].Value
                    .Replace("\\\\", "\\", StringComparison.Ordinal)
                    .Trim();
                if (!string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value))
                {
                    return NormalizePath(value);
                }
            }
        }
        catch
        {
            // A malformed or temporarily locked Unity configuration should not
            // prevent the remaining diagnostic locations from being available.
        }

        return null;
    }

    private static string? ReadEnvironmentPath(
        string variableName,
        bool requireDirectory = true)
    {
        try
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value.Trim()))
            {
                return null;
            }

            var path = NormalizePath(value);
            return !requireDirectory || Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static UnityDiagnosticLocation Create(
        string key,
        string path,
        string source)
    {
        var normalizedPath = NormalizePath(path);
        return new UnityDiagnosticLocation(
            key,
            normalizedPath,
            source,
            Directory.Exists(normalizedPath));
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }
}
