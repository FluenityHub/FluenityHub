namespace FluenityHub_WinUIHost.Models;

public sealed class AppSettings
{
    public bool GroupFavoritesFirst { get; set; } = true;
    public string AppTheme { get; set; } = "Default";
    public int MinimizeBehavior { get; set; } = 0;
    public bool LowerPriorityWhenUnityOpens { get; set; } = false;
    public bool EnableSourceControl { get; set; } = true;
    public bool ExplorerContextMenuEnabled { get; set; } = false;
    public bool AutoResetSandboxOnClose { get; set; } = false;
    public List<string> CustomFavoritePaths { get; set; } = [];
    public List<string> CustomEditorPaths { get; set; } = [];
    public List<string> CustomTemplatePaths { get; set; } = [];
    public Dictionary<string, List<string>> ProjectTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TagColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> TagCategoryOrder { get; set; } = [];
    public string UnityCloudOrganizationId { get; set; } = string.Empty;
    // Display options
    public bool ShowFavoritesColumn { get; set; } = true;
    public bool ShowSourceControlColumn { get; set; } = false;
    public bool ShowModifiedColumn { get; set; } = true;
    public bool ShowEditorVersionColumn { get; set; } = true;
    public bool ShowPlatformColumn { get; set; } = true;
    public bool HideMissingProjects { get; set; } = false;
    public List<string> ProjectEditorFilters { get; set; } = [];
    public List<string> ProjectPlatformFilters { get; set; } = [];
    public List<string> ProjectTagFilters { get; set; } = [];

    // Sort options
    public string SortCriteria { get; set; } = "LastModified";
    public bool SortAscending { get; set; } = false;
    public bool KeepStarredOnTop { get; set; } = true;
    public bool KeepSourceControlOnTop { get; set; } = false;

    // Template page options
    public List<string> TemplateEditorFilters { get; set; } = [];
    public List<string> TemplateTagFilters { get; set; } = [];
    public bool TemplateHideMissingEditors { get; set; } = false;
    public string TemplateSortCriteria { get; set; } = "CreatedAt";
    public bool TemplateSortAscending { get; set; } = false;
}
