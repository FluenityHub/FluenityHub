using System.ComponentModel;
using System.Runtime.CompilerServices;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;

namespace FluenityHub_WinUIHost.Models;

public sealed class ProjectListItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public UnityProjectInfo Project { get; }

    public string Title => Project.Title;

    public string Path => Project.Path;

    public string VersionLabel => Project.Version;

    public bool IsFavorite => Project.IsFavorite;

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734"; // Filled star vs outline star

    public string? GitBranch => Project.GitBranch;
    public bool HasGitBranch => !string.IsNullOrEmpty(GitBranch);
    public bool HasSourceControl => !string.IsNullOrWhiteSpace(Project.SourceControlProvider);
    public bool IsGitBackedSourceControl =>
        string.Equals(Project.SourceControlProvider, "Git", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Project.SourceControlProvider, "GitHub", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Project.SourceControlProvider, "GitLab", StringComparison.OrdinalIgnoreCase);
    public bool HasCloudConnection => !string.IsNullOrWhiteSpace(Project.CloudProjectId)
                                      && !string.IsNullOrWhiteSpace(Project.OrganizationId);
    public bool IsUnityVersionControl => ProjectConnectionService.IsUnityVersionControl(Project);
    public string SourceControlLabel => string.IsNullOrWhiteSpace(Project.SourceControlDetail)
        ? Project.SourceControlProvider ?? string.Empty
        : $"{Project.SourceControlProvider}: {Project.SourceControlDetail}";

    public IReadOnlyList<string> Tags => Project.Tags;
    public bool HasTags => Project.Tags.Count > 0;
    public string PrimaryTag => Project.Tags.Count > 0 ? Project.Tags[0] : string.Empty;
    public string TagBadgeLabel => Project.Tags.Count switch
    {
        0 => string.Empty,
        1 => Project.Tags[0],
        _ => $"{Project.Tags[0]} +{Project.Tags.Count - 1}"
    };
    public bool IsTagBadgeVisible => HasTags;
    private Microsoft.UI.Xaml.Media.SolidColorBrush? _primaryTagColorBrush;
    public Microsoft.UI.Xaml.Media.SolidColorBrush PrimaryTagColorBrush =>
        _primaryTagColorBrush ??= Helpers.TagColorHelper.GetSolidBrushForTag(PrimaryTag);

    private Microsoft.UI.Xaml.Media.SolidColorBrush? _primaryTagTintBrush;
    public Microsoft.UI.Xaml.Media.SolidColorBrush PrimaryTagTintBrush =>
        _primaryTagTintBrush ??= Helpers.TagColorHelper.GetTintBrushForTag(PrimaryTag);

    public string TagsToolTip => HasTags
        ? $"Tags: {string.Join(", ", Project.Tags)}"
        : "No tags set";

    public string Group => string.IsNullOrWhiteSpace(Project.Group) ? "Ungrouped" : Project.Group;

    public void UpdateGroup(string newGroup)
    {
        Project.Group = string.IsNullOrWhiteSpace(newGroup) ? "Ungrouped" : newGroup.Trim();
        OnPropertyChanged(nameof(Group));
    }

    public void UpdateTags(IEnumerable<string> tags)
    {
        Project.Tags.Clear();
        Project.Tags.AddRange(tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Select(t => t.Length > 24 ? t[..24] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        _primaryTagColorBrush = null;
        _primaryTagTintBrush = null;
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(PrimaryTag));
        OnPropertyChanged(nameof(TagBadgeLabel));
        OnPropertyChanged(nameof(IsTagBadgeVisible));
        OnPropertyChanged(nameof(PrimaryTagColorBrush));
        OnPropertyChanged(nameof(PrimaryTagTintBrush));
        OnPropertyChanged(nameof(TagsToolTip));
    }

    private bool _isFavoriteColumnVisible = true;
    public bool IsFavoriteColumnVisible
    {
        get => _isFavoriteColumnVisible;
        set
        {
            if (_isFavoriteColumnVisible != value)
            {
                _isFavoriteColumnVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Column0Width));
            }
        }
    }

    public GridLength Column0Width => new GridLength(IsFavoriteColumnVisible ? 36 : 0);

    private bool _isSourceControlColumnVisible = true;
    public bool IsSourceControlColumnVisible
    {
        get => _isSourceControlColumnVisible;
        set
        {
            if (_isSourceControlColumnVisible != value)
            {
                _isSourceControlColumnVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSourceControlBadgeVisible));
                OnPropertyChanged(nameof(Column1Width));
            }
        }
    }

    private bool _isSourceControlEnabled = true;
    public bool IsSourceControlEnabled
    {
        get => _isSourceControlEnabled;
        set
        {
            if (_isSourceControlEnabled != value)
            {
                _isSourceControlEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSourceControlMenuVisible));
                OnPropertyChanged(nameof(IsConnectSourceControlMenuVisible));
                OnPropertyChanged(nameof(IsDisconnectSourceControlMenuVisible));
                OnPropertyChanged(nameof(IsDisconnectMenuVisible));
                OnPropertyChanged(nameof(IsConnectionMenuVisible));
                OnPropertyChanged(nameof(IsSourceControlBadgeVisible));
                OnPropertyChanged(nameof(Column1Width));
            }
        }
    }

    public Visibility IsSourceControlMenuVisible => IsSourceControlEnabled ? Visibility.Visible : Visibility.Collapsed;

    public bool CanCopyShareLink => IsSourceControlEnabled
                                    && UnityProjectShareLinkService.CanCreate(Project);

    public string CopyShareLinkToolTip => CanCopyShareLink
        ? "Copy a Unity project link"
        : string.IsNullOrWhiteSpace(Project.CloudProjectId)
            || string.IsNullOrWhiteSpace(Project.OrganizationId)
            ? "Connect this project to Unity Cloud to create a link."
            : "Connect this project to GitHub, GitLab, or Unity Version Control to create a link.";

    public GridLength Column1Width => new GridLength((IsSourceControlColumnVisible && IsSourceControlEnabled) ? 36 : 0);

    // The badge is part of the Name cell, not the optional Source Control
    // indicator column. Hiding that column must not hide repository status.
    public bool IsSourceControlBadgeVisible => HasSourceControl && IsSourceControlEnabled;

    public double SourceControlIconOpacity => HasSourceControl ? 1.0 : 0.25;
    public string SourceControlIconGlyph => Project.SourceControlProvider switch
    {
        "Unity Version Control" => "\uE753",
        _ => "\uE71B"
    };
    public string SourceControlToolTip => HasSourceControl
        ? BuildSourceControlToolTip()
        : "Project doesn't use source control.";
    public string DisconnectSourceControlMenuText => $"Disconnect from {Project.SourceControlProvider}";
    public Visibility IsConnectSourceControlMenuVisible => IsSourceControlEnabled && !HasSourceControl
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility IsDisconnectCloudMenuVisible => HasCloudConnection
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility IsDisconnectSourceControlMenuVisible => IsSourceControlEnabled && HasSourceControl
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility IsDisconnectMenuVisible => HasCloudConnection || (IsSourceControlEnabled && HasSourceControl)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility IsConnectionMenuVisible => IsSourceControlEnabled || HasCloudConnection
        ? Visibility.Visible
        : Visibility.Collapsed;

    private string BuildSourceControlToolTip()
    {
        var lines = new List<string> { Project.SourceControlProvider! };
        if (!string.IsNullOrWhiteSpace(Project.SourceControlRepository))
        {
            lines.Add($"Repository: {Project.SourceControlRepository}");
        }
        if (!string.IsNullOrWhiteSpace(Project.SourceControlDetail))
        {
            lines.Add($"Branch: {Project.SourceControlDetail}");
        }
        if (!string.IsNullOrWhiteSpace(Project.SourceControlRevision))
        {
            var revisionLabel = string.Equals(
                    Project.SourceControlProvider,
                    SourceControlDetectionService.UnityVersionControlProvider,
                    StringComparison.OrdinalIgnoreCase)
                ? "Changeset"
                : "Revision";
            lines.Add($"{revisionLabel}: {Project.SourceControlRevision}");
        }

        var isUnityVersionControl = string.Equals(
            Project.SourceControlProvider,
            SourceControlDetectionService.UnityVersionControlProvider,
            StringComparison.OrdinalIgnoreCase);
        lines.Add(isUnityVersionControl
            ? Project.SourceControlHasRemote ? "Connected to Unity Cloud" : "Local Unity Version Control workspace"
            : Project.SourceControlHasRemote ? "Remote: origin configured" : "Local repository");
        return string.Join(Environment.NewLine, lines);
    }

    private bool _isModifiedColumnVisible = true;
    public bool IsModifiedColumnVisible
    {
        get => _isModifiedColumnVisible;
        set
        {
            if (_isModifiedColumnVisible != value)
            {
                _isModifiedColumnVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Column3Width));
            }
        }
    }

    public GridLength Column3Width => new GridLength(IsModifiedColumnVisible ? 140 : 0);

    private bool _isEditorVersionColumnVisible = true;
    public bool IsEditorVersionColumnVisible
    {
        get => _isEditorVersionColumnVisible;
        set
        {
            if (_isEditorVersionColumnVisible != value)
            {
                _isEditorVersionColumnVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Column4Width));
            }
        }
    }

    public GridLength Column4Width => new GridLength(IsEditorVersionColumnVisible ? 130 : 0);

    private bool _isPlatformColumnVisible = true;
    public bool IsPlatformColumnVisible
    {
        get => _isPlatformColumnVisible;
        set
        {
            if (_isPlatformColumnVisible != value)
            {
                _isPlatformColumnVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Column5Width));
            }
        }
    }

    public GridLength Column5Width => new GridLength(IsPlatformColumnVisible ? 175 : 0);

    public string RelativeLastModifiedLabel
    {
        get
        {
            var localTime = Project.LastModifiedUtc.ToLocalTime();
            var timeSpan = DateTime.Now - localTime;

            if (timeSpan.TotalMinutes < 1)
                return "Just now";
            if (timeSpan.TotalMinutes < 60)
                return $"{(int)timeSpan.TotalMinutes}m ago";
            if (timeSpan.TotalHours < 24)
                return $"{(int)timeSpan.TotalHours}h ago";
            if (timeSpan.TotalDays < 7)
                return $"{(int)timeSpan.TotalDays}d ago";

            return localTime.ToString("MMM d, yyyy");
        }
    }

    public string LastModifiedLabel => Project.LastModifiedUtc.ToLocalTime().ToString("g");

    private bool _isEditorInstalled = true;
    public bool IsEditorInstalled
    {
        get => _isEditorInstalled;
        set
        {
            if (_isEditorInstalled != value)
            {
                _isEditorInstalled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsEditorMissing));
                OnPropertyChanged(nameof(EditorStatusLabel));
            }
        }
    }

    public bool IsEditorMissing => !IsEditorInstalled;

    public string EditorStatusLabel => IsEditorInstalled ? "Editor Installed" : "Editor Missing";

    private bool _isLaunching;
    public bool IsLaunching
    {
        get => _isLaunching;
        set
        {
            if (_isLaunching == value)
            {
                return;
            }

            _isLaunching = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaunchStatusText));
        }
    }

    public string LaunchStatusText => IsLaunching ? "Opening..." : string.Empty;

    /// <summary>
    /// List of all installed Unity editor version strings for the ComboBox.
    /// </summary>
    public List<string> InstalledEditorVersions { get; private set; }

    public List<TargetPlatformInfo> AvailableTargetPlatforms { get; private set; } = [];

    private TargetPlatformInfo? _selectedTargetPlatform;
    public TargetPlatformInfo? SelectedTargetPlatform
    {
        get => _selectedTargetPlatform;
        set
        {
            if (value is null || string.Equals(_selectedTargetPlatform?.Id, value.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedTargetPlatform = value;
            Project.BuildTarget = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTargetPlatformId));
        }
    }

    public string? SelectedTargetPlatformId =>
        string.IsNullOrWhiteSpace(_selectedTargetPlatform?.Id) ? null : _selectedTargetPlatform.Id;

    private string _selectedEditorVersion;
    /// <summary>
    /// The currently selected editor version for this project.
    /// </summary>
    public string SelectedEditorVersion
    {
        get => _selectedEditorVersion;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!string.Equals(_selectedEditorVersion, value, StringComparison.OrdinalIgnoreCase))
            {
                _selectedEditorVersion = value;
                Project.Version = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VersionLabel));
            }
        }
    }

    public ProjectListItemViewModel(
        UnityProjectInfo project,
        bool isEditorInstalled = true,
        IEnumerable<string>? installedEditorVersions = null,
        IEnumerable<TargetPlatformInfo>? availableTargetPlatforms = null)
    {
        Project = project;
        _isEditorInstalled = isEditorInstalled;

        var versions = installedEditorVersions?.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        // Ensure the project's own version is in the list even if not installed
        if (!string.IsNullOrWhiteSpace(project.Version)
            && !versions.Contains(project.Version, StringComparer.OrdinalIgnoreCase))
        {
            versions.Insert(0, project.Version);
        }

        InstalledEditorVersions = versions;
        _selectedEditorVersion = project.Version;
        RefreshAvailableTargetPlatforms(availableTargetPlatforms ?? []);
    }

    public void RefreshRuntimeState(
        bool isEditorInstalled,
        IEnumerable<string> installedEditorVersions,
        IEnumerable<TargetPlatformInfo> availableTargetPlatforms)
    {
        IsEditorInstalled = isEditorInstalled;

        var versions = installedEditorVersions
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(SelectedEditorVersion)
            && !versions.Contains(SelectedEditorVersion, StringComparer.OrdinalIgnoreCase))
        {
            versions.Insert(0, SelectedEditorVersion);
        }

        InstalledEditorVersions = versions;
        OnPropertyChanged(nameof(InstalledEditorVersions));

        // Replacing a Selector's ItemsSource clears its current selection. The
        // selected version itself did not change, so notify it explicitly after
        // the new source is installed and let the OneWay binding restore the row.
        OnPropertyChanged(nameof(SelectedEditorVersion));
        RefreshAvailableTargetPlatforms(availableTargetPlatforms);

        OnPropertyChanged(nameof(HasGitBranch));
        OnPropertyChanged(nameof(GitBranch));
        OnPropertyChanged(nameof(HasSourceControl));
        OnPropertyChanged(nameof(IsGitBackedSourceControl));
        OnPropertyChanged(nameof(SourceControlLabel));
        OnPropertyChanged(nameof(IsSourceControlBadgeVisible));
        OnPropertyChanged(nameof(SourceControlIconOpacity));
        OnPropertyChanged(nameof(SourceControlIconGlyph));
        OnPropertyChanged(nameof(SourceControlToolTip));
        OnPropertyChanged(nameof(HasCloudConnection));
        OnPropertyChanged(nameof(IsUnityVersionControl));
        OnPropertyChanged(nameof(DisconnectSourceControlMenuText));
        OnPropertyChanged(nameof(IsConnectSourceControlMenuVisible));
        OnPropertyChanged(nameof(IsDisconnectCloudMenuVisible));
        OnPropertyChanged(nameof(IsDisconnectSourceControlMenuVisible));
        OnPropertyChanged(nameof(IsDisconnectMenuVisible));
        OnPropertyChanged(nameof(IsConnectionMenuVisible));
        OnPropertyChanged(nameof(CanCopyShareLink));
        OnPropertyChanged(nameof(CopyShareLinkToolTip));
    }

    public void RefreshAvailableTargetPlatforms(IEnumerable<TargetPlatformInfo> platforms)
    {
        var choices = platforms
            .Where(platform => !string.IsNullOrWhiteSpace(platform.Id))
            .DistinctBy(platform => platform.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var preferredId = NormalizeTargetPlatformId(_selectedTargetPlatform?.Id ?? Project.BuildTarget);
        if (choices.Count == 0 && !string.IsNullOrWhiteSpace(preferredId))
        {
            choices.Insert(0, CreateFallbackTargetPlatform(preferredId));
        }

        if (choices.Count == 0)
        {
            choices.Add(new TargetPlatformInfo("StandaloneWindows64", "Windows (64-bit)", "\uE74C"));
        }

        AvailableTargetPlatforms = choices;
        _selectedTargetPlatform = choices.FirstOrDefault(platform =>
                                      string.Equals(platform.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                                  ?? choices[0];
        Project.BuildTarget = _selectedTargetPlatform.Id;

        OnPropertyChanged(nameof(AvailableTargetPlatforms));
        OnPropertyChanged(nameof(SelectedTargetPlatform));
        OnPropertyChanged(nameof(SelectedTargetPlatformId));
    }

    private static TargetPlatformInfo CreateFallbackTargetPlatform(string id)
        => id switch
        {
            "StandaloneWindows64" => new(id, "Windows (64-bit)", "\uE74C"),
            "StandaloneWindows" => new(id, "Windows", "\uE74C"),
            "Android" => new(id, "Android", "\uE702"),
            "iOS" => new(id, "iOS", "\uE70A"),
            "WebGL" => new(id, "WebGL", "\uE774"),
            "StandaloneOSX" => new(id, "macOS", "\uE7F1"),
            "StandaloneLinux64" => new(id, "Linux", "\uE748"),
            _ => new(id, id, "\uE7F4")
        };

    private static string NormalizeTargetPlatformId(string? id)
        => id?.Trim() switch
        {
            "StandaloneWindows" => "StandaloneWindows64",
            "WebGLPlayer" => "WebGL",
            "iPhone" => "iOS",
            var value => value ?? string.Empty
        };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

