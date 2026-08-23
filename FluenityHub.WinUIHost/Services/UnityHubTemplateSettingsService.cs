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

    /// <summary>
    /// Registers or updates a custom template in Unity Hub's <c>templatesSettings.json</c> <c>sources</c> map.
    /// Unity Hub uses this map to track known templates, their source project paths,
    /// and signing organization identifiers. Without this entry, Unity Hub may not
    /// fully recognize templates created or updated outside of its own UI.
    /// </summary>
    public void RegisterTemplateSource(
        string tgzPath,
        string? sourceProjectPath = null,
        string? editorVersion = null)
    {
        try
        {
            var settings = ReadSettings() ?? new JsonObject();
            if (settings["sources"] is not JsonObject sources)
            {
                sources = new JsonObject();
                settings["sources"] = sources;
            }

            var normalizedTgzPath = Path.GetFullPath(tgzPath);
            var existing = sources[normalizedTgzPath] as JsonObject;

            var finalProjectPath = !string.IsNullOrWhiteSpace(sourceProjectPath)
                ? sourceProjectPath
                : existing?["projectPath"]?.GetValue<string>() ?? string.Empty;

            var finalEditorVersion = !string.IsNullOrWhiteSpace(editorVersion)
                ? editorVersion
                : existing?["editorVersion"]?.GetValue<string>() ?? string.Empty;

            var signingOrgId = existing?["signingOrganizationId"]?.GetValue<string>();

            var entry = new JsonObject
            {
                ["id"] = normalizedTgzPath,
                ["projectPath"] = finalProjectPath,
                ["path"] = normalizedTgzPath,
                ["lastChecked"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["lastUsed"] = existing?["lastUsed"]?.GetValue<long>() ?? -1,
                ["editorVersion"] = finalEditorVersion,
                ["disabled"] = false
            };

            if (!string.IsNullOrWhiteSpace(signingOrgId))
            {
                entry["signingOrganizationId"] = signingOrgId;
            }

            sources[normalizedTgzPath] = entry;
            WriteSettingsAtomically(settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to register template source in Unity Hub: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes a custom template from Unity Hub's <c>templatesSettings.json</c> <c>sources</c> map.
    /// </summary>
    public void UnregisterTemplateSource(string tgzPath)
    {
        try
        {
            var settings = ReadSettings();
            if (settings?["sources"] is not JsonObject sources)
            {
                return;
            }

            var normalizedTgzPath = Path.GetFullPath(tgzPath);
            if (sources.Remove(normalizedTgzPath))
            {
                WriteSettingsAtomically(settings);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Failed to unregister template source in Unity Hub: {ex.Message}");
        }
    }

    /// <summary>
    /// Bumps the <c>lastChecked</c> timestamp for a template in Unity Hub's <c>sources</c>,
    /// signaling Unity Hub to refresh its cached data for the template.
    /// </summary>
    public void TouchTemplateSource(string tgzPath)
    {
        RegisterTemplateSource(tgzPath);
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
