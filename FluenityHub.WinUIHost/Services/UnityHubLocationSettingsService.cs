using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Reads and writes the location preferences shared with Unity Hub.
/// Unity Hub stores these values as small JSON files under its roaming data directory.
/// </summary>
public sealed class UnityHubLocationSettingsService
{
    private static readonly string UnityHubDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub");

    public static string InstallLocationFilePath { get; } =
        Path.Combine(UnityHubDirectory, "secondaryInstallPath.json");

    public static string DownloadLocationFilePath { get; } =
        Path.Combine(UnityHubDirectory, "secondaryDownloadLocation.json");

    public static string DefaultInstallLocation { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Unity",
        "Hub",
        "Editor");

    public static string DefaultDownloadLocation { get; } = Path.Combine(
        UnityHubDirectory,
        "downloads");

    public string GetInstallLocation(string? fallback = null)
    {
        try
        {
            var node = ReadNode(InstallLocationFilePath);
            if (node is JsonValue value
                && value.TryGetValue<string>(out var configuredPath)
                && !string.IsNullOrWhiteSpace(configuredPath))
            {
                return NormalizePath(configuredPath);
            }
        }
        catch
        {
            // A malformed or temporarily locked Hub setting should not block the app.
        }

        return NormalizePath(
            string.IsNullOrWhiteSpace(fallback) ? DefaultInstallLocation : fallback);
    }

    public string GetDownloadLocation()
    {
        try
        {
            var node = ReadNode(DownloadLocationFilePath);
            if (node?["path"] is JsonValue value
                && value.TryGetValue<string>(out var configuredPath)
                && !string.IsNullOrWhiteSpace(configuredPath))
            {
                return NormalizePath(configuredPath);
            }
        }
        catch
        {
            // A malformed or temporarily locked Hub setting should not block the app.
        }

        return DefaultDownloadLocation;
    }

    public void SetInstallLocation(string path)
    {
        var normalizedPath = NormalizePath(path);
        WriteNodeAtomically(
            InstallLocationFilePath,
            JsonValue.Create(normalizedPath)
                ?? throw new InvalidOperationException("The install location is invalid."));
    }

    public void SetDownloadLocation(string path)
    {
        var normalizedPath = NormalizePath(path);
        WriteNodeAtomically(
            DownloadLocationFilePath,
            new JsonObject { ["path"] = normalizedPath });
    }

    private static JsonNode? ReadNode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream);
    }

    private static void WriteNodeAtomically(string path, JsonNode value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Unity Hub settings directory is invalid.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                value.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // The final setting is already committed; stale temp cleanup is best effort.
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Choose a folder.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    }
}
