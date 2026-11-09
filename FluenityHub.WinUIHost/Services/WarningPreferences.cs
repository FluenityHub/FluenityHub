namespace FluenityHub_WinUIHost.Services;

internal static class WarningPreferences
{
    public static string ProjectKey(string path, string version)
        => $"project-editor:{Path.TrimEndingDirectorySeparator(path).ToUpperInvariant()}:{version.ToUpperInvariant()}";

    public static string TemplateKey(string id, string version)
        => $"template-editor:{id}:{version.ToUpperInvariant()}";

    public static void SetDismissed(string key, bool dismissed)
    {
        new AppSettingsStore().UpdatePreferences(settings =>
        {
            settings.DismissedWarnings.RemoveAll(value => string.Equals(value, key, StringComparison.Ordinal));
            if (dismissed) settings.DismissedWarnings.Add(key);
        });
    }
}
