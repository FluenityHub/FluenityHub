using System.Text.Json;
using System.Text.Json.Nodes;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class AppSettingsStore
{
    private static readonly Lock SettingsGate = new();

    private readonly string _settingsFilePath;

    public AppSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluenityHub.WinUIHost", "settings.json"))
    {
    }

    internal AppSettingsStore(string settingsFilePath)
    {
        _settingsFilePath = Path.GetFullPath(settingsFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)
            ?? throw new ArgumentException("A settings directory is required.", nameof(settingsFilePath)));
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        string content;
        using (var stream = File.Open(_settingsFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            content = reader.ReadToEnd();
        MigrateLegacyTokenFields(content);

        var settings = JsonSerializer.Deserialize(content, AppJsonContext.Default.AppSettings);
        if (settings is null)
        {
            throw new InvalidDataException("Settings file is empty or invalid JSON.");
        }

        return settings;
    }

    public void Save(AppSettings settings, bool updateGitAccounts = false)
    {
        lock (SettingsGate)
        {
            if (File.Exists(_settingsFilePath))
            {
                var current = Load();
                if (!updateGitAccounts) settings.GitAccounts = current.GitAccounts;
                settings.DismissedWarnings = current.DismissedWarnings;
            }
            Write(settings);
        }
    }

    public void UpdatePreferences(Action<AppSettings> update)
    {
        lock (SettingsGate)
        {
            var settings = Load();
            update(settings);
            Write(settings);
        }
    }

    private void Write(AppSettings settings)
    {
        var content = JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        var temporaryPath = _settingsFilePath + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, _settingsFilePath, overwrite: true);
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
