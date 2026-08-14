using System.Text.Json;
using System.Text.Json.Nodes;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class AppSettingsStore
{

    private readonly string _settingsFilePath;

    public AppSettingsStore()
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FluenityHub.WinUIHost");
        Directory.CreateDirectory(appDataDirectory);
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        var content = File.ReadAllText(_settingsFilePath);
        MigrateLegacyTokenFields(content);

        var settings = JsonSerializer.Deserialize(content, AppJsonContext.Default.AppSettings);
        if (settings is null)
        {
            throw new InvalidDataException("Settings file is empty or invalid JSON.");
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        var content = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        File.WriteAllText(_settingsFilePath, content);
    }

    private void MigrateLegacyTokenFields(string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var root = JsonNode.Parse(content)?.AsObject();
            if (root is null)
            {
                return;
            }

            var changed = false;
            if (root.TryGetPropertyValue("GitHubToken", out var gitHubTokenNode))
            {
                var token = gitHubTokenNode?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(CredentialService.GetGitHubToken()))
                {
                    CredentialService.SaveGitHubToken(token);
                }

                root.Remove("GitHubToken");
                changed = true;
            }

            if (root.TryGetPropertyValue("GitLabToken", out var gitLabTokenNode))
            {
                var token = gitLabTokenNode?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(token) && string.IsNullOrWhiteSpace(CredentialService.GetGitLabToken()))
                {
                    CredentialService.SaveGitLabToken(token);
                }

                root.Remove("GitLabToken");
                changed = true;
            }

            if (changed)
            {
                File.WriteAllText(_settingsFilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // Settings loading should not fail only because legacy token cleanup failed.
        }
    }
}
