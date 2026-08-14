using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Reads and writes Unity Hub's project preferences without maintaining a
/// disconnected FluenityHub copy.
/// </summary>
public sealed class UnityHubProjectSettingsService
{
    private static readonly string UnityHubDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub");

    public static string ProjectLocationFilePath { get; } =
        Path.Combine(UnityHubDirectory, "projectDir.json");

    public static string UserSettingsFilePath { get; } =
        Path.Combine(UnityHubDirectory, "user-settings.json");

    public static string DefaultProjectLocation { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string GetProjectLocation()
    {
        try
        {
            var root = ReadObject(ProjectLocationFilePath);
            var configuredPath = root?["directoryPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return NormalizePath(configuredPath);
            }
        }
        catch
        {
            // A malformed or temporarily locked Hub setting should not block the app.
        }

        return DefaultProjectLocation;
    }

    public void SetProjectLocation(string path)
    {
        WriteObjectAtomically(
            ProjectLocationFilePath,
            new JsonObject { ["directoryPath"] = NormalizePath(path) });
    }

    public bool GetShowProductNames()
    {
        try
        {
            var root = ReadObject(UserSettingsFilePath);
            if (root?["projects"]?["showProductNames"] is JsonValue value
                && value.TryGetValue<bool>(out var showProductNames))
            {
                return showProductNames;
            }
        }
        catch
        {
            // Unity Hub's documented default is the folder name.
        }

        return false;
    }

    public void SetShowProductNames(bool showProductNames)
    {
        JsonObject root;
        try
        {
            root = ReadObject(UserSettingsFilePath) ?? new JsonObject();
        }
        catch
        {
            throw new InvalidDataException(
                "Unity Hub's user-settings.json is not valid JSON.");
        }

        if (root["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            root["projects"] = projects;
        }

        projects["showProductNames"] = showProductNames;
        WriteObjectAtomically(UserSettingsFilePath, root, writeIndented: true);
    }

    public bool GetClearTokensOnLogout()
    {
        try
        {
            var root = ReadObject(UserSettingsFilePath);
            if (root?["security"]?["clearTokensOnLogout"] is JsonValue value
                && value.TryGetValue<bool>(out var clearTokensOnLogout))
            {
                return clearTokensOnLogout;
            }
        }
        catch
        {
            // Keep credentials unless the shared Unity Hub preference explicitly
            // requests removal.
        }

        return false;
    }

    public void SetClearTokensOnLogout(bool clearTokensOnLogout)
    {
        JsonObject root;
        try
        {
            root = ReadObject(UserSettingsFilePath) ?? new JsonObject();
        }
        catch
        {
            throw new InvalidDataException(
                "Unity Hub's user-settings.json is not valid JSON.");
        }

        if (root["security"] is not JsonObject security)
        {
            security = new JsonObject();
            root["security"] = security;
        }

        security["clearTokensOnLogout"] = clearTokensOnLogout;
        WriteObjectAtomically(UserSettingsFilePath, root, writeIndented: true);
    }

    private static JsonObject? ReadObject(string path)
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
        return JsonNode.Parse(stream)?.AsObject();
    }

    private static void WriteObjectAtomically(
        string path,
        JsonObject value,
        bool writeIndented = false)
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
                value.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = writeIndented }));
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
                // The setting is already committed; temp cleanup is best effort.
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
