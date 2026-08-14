using System.Text.Json.Serialization;

namespace FluenityHub_WinUIHost.Models;

public sealed class ProjectBackupRecord
{
    public string Id { get; init; } = string.Empty;

    public string SourceProjectPath { get; init; } = string.Empty;

    public string ProjectTitle { get; init; } = string.Empty;

    public string UnityVersion { get; init; } = string.Empty;

    public string BackupPath { get; init; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public long TotalBytes { get; init; }

    public bool IncludesUserSettings { get; init; }

    public bool IncludesGitHistory { get; init; }

    [JsonIgnore]
    public bool IsAvailable => Directory.Exists(BackupPath);

    [JsonIgnore]
    public string CreatedAtDisplay => CreatedAtUtc.ToLocalTime().ToString("g");

    [JsonIgnore]
    public string SizeDisplay => FormatBytes(TotalBytes);

    [JsonIgnore]
    public string LocationDisplay => IsAvailable ? BackupPath : $"{BackupPath} (missing)";

    [JsonIgnore]
    public string DeleteActionText => IsAvailable ? "Delete backup" : "Remove missing record";

    [JsonIgnore]
    public string DetailsDisplay
    {
        get
        {
            var options = new List<string>();
            if (IncludesUserSettings)
            {
                options.Add("UserSettings");
            }

            if (IncludesGitHistory)
            {
                options.Add("Git history");
            }

            var optionsText = options.Count == 0 ? "Project files" : string.Join(" · ", options);
            return $"{SizeDisplay} · {optionsText}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = Math.Clamp((int)(Math.Log(bytes, 1024)), 0, units.Length - 1);
        var value = bytes / Math.Pow(1024, unit);
        return $"{value:0.##} {units[unit]}";
    }
}

public sealed record ProjectCopyProgress(
    string Status,
    string CurrentItem,
    long BytesCopied,
    long TotalBytes,
    int FilesCopied,
    int TotalFiles)
{
    public double? Percentage => TotalBytes > 0
        ? Math.Clamp(BytesCopied * 100d / TotalBytes, 0, 100)
        : TotalFiles > 0
            ? Math.Clamp(FilesCopied * 100d / TotalFiles, 0, 100)
            : null;
}
