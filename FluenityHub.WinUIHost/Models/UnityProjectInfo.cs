namespace FluenityHub_WinUIHost.Models;

public sealed class UnityProjectInfo
{
    public string Path { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string BuildTarget { get; set; } = string.Empty;

    public string? CloudProjectId { get; init; }

    public string? OrganizationId { get; init; }

    public string? LocalProjectId { get; init; }

    public DateTime LastModifiedUtc { get; init; } = DateTime.UtcNow;

    public bool IsFavorite { get; set; } = false;

    public string? GitBranch { get; set; }

    public string? SourceControlProvider { get; set; }

    public string? SourceControlDetail { get; set; }

    public string? SourceControlRevision { get; set; }

    public string? SourceControlRemoteUrl { get; set; }

    public string? SourceControlRepository { get; set; }

    public bool SourceControlHasRemote { get; set; }

    public string? ConfiguredSourceControlProvider { get; init; }

    public string? ConfiguredSourceControlOrganization { get; init; }

    public string? ConfiguredSourceControlRepository { get; init; }

    public string? ProjectPathInsideRepository { get; init; }

    public bool IsSourceControlDisconnected { get; init; }

    public string? CommandLineArguments { get; set; }

    public List<string> Tags { get; set; } = [];

    public string Group { get; set; } = "Ungrouped";
}
