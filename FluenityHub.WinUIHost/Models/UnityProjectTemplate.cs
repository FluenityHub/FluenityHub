namespace FluenityHub_WinUIHost.Models;

internal sealed record UnityProjectTemplate(
    string PackageName,
    string Name,
    string Category,
    string Description,
    string Version,
    string? ArchivePath = null,
    Uri? DownloadUri = null,
    long DownloadSize = 0,
    string? ArchiveSha1 = null,
    Uri? PreviewUri = null)
{
    public CustomTemplateInfo? CustomTemplate { get; init; }
    public string DisplayName => Category.Length == 0 ? Name : $"{Name} ({Category})";
    public string CardCategory => Category.Length == 0 ? "Blank" : Category;
    public bool HasVersion => Version.Length != 0;
    public override string ToString() => DisplayName;
}
