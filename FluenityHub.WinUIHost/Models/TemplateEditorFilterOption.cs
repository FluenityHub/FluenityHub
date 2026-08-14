namespace FluenityHub_WinUIHost.Models;

public sealed class TemplateEditorFilterOption
{
    public string Version { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }
    public bool IsMissing => !IsInstalled;
    public bool IsSelected { get; set; }
}
