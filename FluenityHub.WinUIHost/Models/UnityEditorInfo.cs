namespace FluenityHub_WinUIHost.Models;

public sealed class UnityEditorInfo
{
    public required string Version { get; init; }
    public required string ExecutablePath { get; init; }
    public required string InstallDirectory { get; init; }
    public string Architecture { get; init; } = "x64 (Windows)";
    public string? IconPath { get; init; }
    public List<TargetPlatformInfo> InstalledTargetPlatforms { get; init; } = [];
    public string DisplayName => FormatDisplayName(Version);

    private static string FormatDisplayName(string version)
    {
        var components = version.Split('.');
        if (components.Length < 2)
        {
            return $"Unity {version}";
        }

        var productVersion = components[0] switch
        {
            "6000" => components[1] == "0" ? "6" : $"6.{components[1]}",
            _ => $"{components[0]}.{components[1]}"
        };
        var channel = version.Contains('a', StringComparison.OrdinalIgnoreCase)
            ? " Alpha"
            : version.Contains('b', StringComparison.OrdinalIgnoreCase)
                ? " Beta"
                : string.Empty;

        return $"Unity {productVersion}{channel} ({version})";
    }
}
