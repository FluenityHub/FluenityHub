using System.Drawing;
using System.Drawing.Imaging;
using FluenityHub_WinUIHost.Models;
using Microsoft.Win32;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityEditorLocator
{
    private static readonly StringComparer VersionComparer = StringComparer.OrdinalIgnoreCase;

    public Dictionary<string, string> GetInstalledEditors(IEnumerable<string>? customPaths = null)
    {
        var editors = new Dictionary<string, string>(VersionComparer);
        LoadFromRegistry(editors);
        LoadFromDefaultEditorDirectory(editors);

        if (customPaths is not null)
        {
            foreach (var customPath in customPaths)
            {
                LoadFromCustomDirectory(customPath, editors);
            }
        }

        return editors;
    }

    public List<UnityEditorInfo> GetInstalledEditorDetails(IEnumerable<string>? customPaths = null)
    {
        var editors = GetInstalledEditors(customPaths);
        var details = new List<UnityEditorInfo>();

        foreach (var (version, executablePath) in editors)
        {
            var installDir = Path.GetDirectoryName(Path.GetDirectoryName(executablePath)) ?? Path.GetDirectoryName(executablePath) ?? string.Empty;
            var iconPath = EnsureIconExtracted(version, executablePath);

            var platforms = GetInstalledTargetPlatforms(executablePath);
            details.Add(new UnityEditorInfo
            {
                Version = version,
                ExecutablePath = executablePath,
                InstallDirectory = installDir,
                Architecture = "x64 (Windows)",
                IconPath = iconPath,
                InstalledTargetPlatforms = platforms
            });
        }

        return details.OrderByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<TargetPlatformInfo> GetInstalledTargetPlatforms(string executablePath)
    {
        var platforms = new List<TargetPlatformInfo>
        {
            // Windows (64-bit) is standard on all Windows Unity Editor installs
            new TargetPlatformInfo("StandaloneWindows64", "Windows (64-bit)", "\uE74C")
        };

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return platforms;
        }

        var editorDir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(editorDir)) return platforms;

        var playbackEngines = Path.Combine(editorDir, "Data", "PlaybackEngines");
        if (Directory.Exists(playbackEngines))
        {
            if (Directory.Exists(Path.Combine(playbackEngines, "AndroidPlayer")))
            {
                platforms.Add(new TargetPlatformInfo("Android", "Android", "\uE702"));
            }
            if (Directory.Exists(Path.Combine(playbackEngines, "iOSSupport")))
            {
                platforms.Add(new TargetPlatformInfo("iOS", "iOS", "\uE70A"));
            }
            if (Directory.Exists(Path.Combine(playbackEngines, "WebGLSupport")))
            {
                platforms.Add(new TargetPlatformInfo("WebGL", "WebGL", "\uE774"));
            }
            if (Directory.Exists(Path.Combine(playbackEngines, "MacStandaloneSupport")))
            {
                platforms.Add(new TargetPlatformInfo("StandaloneOSX", "macOS", "\uE7F1"));
            }
            if (Directory.Exists(Path.Combine(playbackEngines, "LinuxStandaloneSupport")))
            {
                platforms.Add(new TargetPlatformInfo("StandaloneLinux64", "Linux", "\uE748"));
            }
        }

        return platforms;
    }

    public string? FindEditorExecutable(string version, IReadOnlyDictionary<string, string> installedEditors)
    {
        if (installedEditors.TryGetValue(version, out var exactMatch))
        {
            return exactMatch;
        }

        foreach (var (installedVersion, executablePath) in installedEditors)
        {
            if (installedVersion.StartsWith(version, StringComparison.OrdinalIgnoreCase)
                || version.StartsWith(installedVersion, StringComparison.OrdinalIgnoreCase))
            {
                return executablePath;
            }
        }

        return null;
    }

    private static string? EnsureIconExtracted(string version, string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FluenityHub",
                "Cache",
                "Icons");

            Directory.CreateDirectory(cacheDir);

            var safeVersion = string.Concat(version.Split(Path.GetInvalidFileNameChars()));
            var iconPngPath = Path.Combine(cacheDir, $"{safeVersion}.png");

            if (File.Exists(iconPngPath))
            {
                return iconPngPath;
            }

            using var sysIcon = Icon.ExtractAssociatedIcon(executablePath);
            if (sysIcon is null)
            {
                return null;
            }

            using var bitmap = sysIcon.ToBitmap();
            bitmap.Save(iconPngPath, ImageFormat.Png);
            return iconPngPath;
        }
        catch
        {
            return null;
        }
    }

    private static void LoadFromRegistry(IDictionary<string, string> editors)
    {
        LoadFromRegistryHive(Registry.CurrentUser, editors);
        LoadFromRegistryHive(Registry.LocalMachine, editors);
    }

    private static void LoadFromRegistryHive(RegistryKey hive, IDictionary<string, string> editors)
    {
        using var installerRoot = hive.OpenSubKey(@"SOFTWARE\Unity Technologies\Installer");
        if (installerRoot is null)
        {
            return;
        }

        foreach (var subKeyName in installerRoot.GetSubKeyNames())
        {
            using var subKey = installerRoot.OpenSubKey(subKeyName);
            if (subKey is null)
            {
                continue;
            }

            var version = subKey.GetValue("Version") as string;
            var installPath = (subKey.GetValue("Location x64") as string) ?? (subKey.GetValue("Location") as string);
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(installPath))
            {
                continue;
            }

            var executablePath = Path.Combine(installPath, "Editor", "Unity.exe");
            if (File.Exists(executablePath))
            {
                editors[version] = executablePath;
            }
        }
    }

    private static void LoadFromDefaultEditorDirectory(IDictionary<string, string> editors)
    {
        var sharedEditorDirectory =
            new UnityHubLocationSettingsService().GetInstallLocation();
        LoadFromCustomDirectory(sharedEditorDirectory, editors);

        var legacyEditorDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Unity",
            "Hub",
            "Editor");
        if (!string.Equals(
                sharedEditorDirectory,
                legacyEditorDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            LoadFromCustomDirectory(legacyEditorDirectory, editors);
        }
    }

    private static void LoadFromCustomDirectory(string directoryPath, IDictionary<string, string> editors)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        // Direct Unity.exe check inside specified directory
        var directExe = Path.Combine(directoryPath, "Editor", "Unity.exe");
        if (File.Exists(directExe))
        {
            var dirName = Path.GetFileName(directoryPath);
            editors[dirName] = directExe;
            return;
        }

        // Scan subdirectories
        foreach (var editorDirectory in Directory.GetDirectories(directoryPath))
        {
            var version = Path.GetFileName(editorDirectory);
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            var executablePath = Path.Combine(editorDirectory, "Editor", "Unity.exe");
            if (File.Exists(executablePath))
            {
                editors[version] = executablePath;
            }
        }
    }
}
