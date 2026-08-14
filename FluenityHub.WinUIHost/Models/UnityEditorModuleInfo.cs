using System.Collections.ObjectModel;

namespace FluenityHub_WinUIHost.Models;

public sealed class UnityEditorModuleInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string ParentId { get; init; } = string.Empty;
    public long DownloadSizeBytes { get; init; }
    public long InstalledSizeBytes { get; init; }
    public bool IsInstalled { get; internal set; }
    public bool IsRequired { get; init; }
    public bool IsPreselected { get; init; }
    public bool CanRemove { get; internal set; }
    internal string Destination { get; init; } = string.Empty;
    internal string RenameTo { get; init; } = string.Empty;
    internal string SyncId { get; init; } = string.Empty;
    public IReadOnlyList<UnityLicenseTerm> LicenseTerms { get; init; } = [];

    public bool IsChild => !string.IsNullOrWhiteSpace(ParentId);
    public bool IsSelectable => !IsInstalled;
    public bool HasLicenseTerms => LicenseTerms.Count > 0;
    public string DownloadSizeLabel => IsInstalled ? "Installed" : FormatBytes(DownloadSizeBytes);
    public string InstalledSizeLabel => FormatBytes(InstalledSizeBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }
}

public sealed class UnityLicenseTerm
{
    public required string ModuleId { get; init; }
    public required string ModuleName { get; init; }
    public required string Label { get; init; }
    public string Message { get; init; } = string.Empty;
    public Uri? NavigateUri { get; init; }
}

public sealed class UnityModuleTreeItem
{
    public required string Name { get; init; }
    public UnityEditorModuleInfo? Module { get; init; }
    public bool IsCategory { get; init; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<UnityModuleTreeItem> Children { get; } = [];
    public bool CanSelect => Module?.IsSelectable == true
        || (IsCategory && Children.Any(child => child.CanSelect));
    public string DownloadSizeLabel => Module?.DownloadSizeLabel ?? string.Empty;
    public string InstalledSizeLabel => Module?.InstalledSizeLabel ?? string.Empty;
    public bool IsInstalled => Module?.IsInstalled == true;
    public bool CanRepair => Module?.IsInstalled == true;
    public bool CanRemove => Module?.CanRemove == true;
    public bool ShowMaintenanceActions => Module?.IsInstalled == true;
}
