using System.Text.Json.Nodes;
using System.Text.Json;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityHubTemplateSettingsService
{
    private static readonly string UnityHubDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub");

    public static string SettingsFilePath { get; } =
        Path.Combine(UnityHubDirectory, "templatesSettings.json");

    public static string DefaultTemplatesPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Unity user templates");

    public string GetCurrentPath()
    {
        try
        {
            var settings = ReadSettings();
            if (settings?["defaultPath"] is JsonValue pathValue &&
                pathValue.TryGetValue<string>(out var configuredPath) &&
                !string.IsNullOrWhiteSpace(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }
        }
        catch
        {
            // A missing or malformed Unity Hub setting must not prevent templates from loading.
        }

        return DefaultTemplatesPath;
    }

    public void SetCurrentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Choose a folder.", nameof(path));
        }

        JsonObject settings;
        try
        {
            settings = ReadSettings() ?? new JsonObject();
        }
        catch
        {
            throw new InvalidDataException(
                "Unity Hub's templatesSettings.json is not valid JSON.");
        }

        settings["defaultPath"] = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path.Trim()));
        WriteSettingsAtomically(settings);
    }

    private static JsonObject? ReadSettings()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return null;
        }

        using var stream = new FileStream(
            SettingsFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)?.AsObject();
    }

    private static void WriteSettingsAtomically(JsonObject settings)
    {
        var directory = Path.GetDirectoryName(SettingsFilePath)
            ?? throw new InvalidOperationException("The Unity Hub settings directory is invalid.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                settings.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = false }));
            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
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
                // The setting is committed; temp cleanup is best effort.
            }
        }
    }
}
