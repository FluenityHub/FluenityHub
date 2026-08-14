using System.Collections.ObjectModel;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed class UnityEditorReleaseListItem
{
    public UnityEditorRelease Release { get; set; } =
        new(string.Empty, DateTimeOffset.MinValue, string.Empty, false, null, null, 0, 0, []);
    public bool IsInstalled { get; set; }
    public string DisplayName =>
        $"Unity {FormatVersion(Release.Version)}{FormatChannelSuffix(Release.Stream)} ({Release.Version})";
    public string StreamBadge => Release.Stream.ToUpperInvariant() switch
    {
        "SUPPORTED" => "Supported",
        "LTS" => "LTS",
        "ALPHA" => "Alpha",
        "BETA" => "Beta",
        _ => string.Empty
    };
    public Visibility NeutralStreamBadgeVisibility =>
        StreamBadge is "Supported" or "LTS" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BetaBadgeVisibility =>
        StreamBadge == "Beta" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AlphaBadgeVisibility =>
        StreamBadge == "Alpha" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecommendedVisibility =>
        Release.IsRecommended ? Visibility.Visible : Visibility.Collapsed;
    public bool CanInstall => !IsInstalled;
    public string InstallButtonText => IsInstalled ? "Installed" : "Install";
    public string ReleaseSummary =>
        $"{Release.ReleaseDate:MMM d, yyyy} · {FormatBytes(Release.DownloadSizeBytes)}";
    public Uri ReleaseNotesUri =>
        new($"https://unity.com/releases/editor/whats-new/{Uri.EscapeDataString(Release.Version)}#notes");
    public string ReleaseNotesAutomationName => $"Open release notes for Unity {Release.Version}";
    public string InstallAutomationName =>
        IsInstalled ? $"Unity {Release.Version} is installed" : $"Install Unity {Release.Version}";

    private static string FormatVersion(string version)
    {
        var components = version.Split('.');
        if (components.Length < 2)
        {
            return version;
        }

        return components[0] switch
        {
            "6000" => components[1] == "0" ? "6" : $"6.{components[1]}",
            _ => $"{components[0]}.{components[1]}"
        };
    }

    private static string FormatChannelSuffix(string stream)
        => stream.ToUpperInvariant() switch
        {
            "LTS" => " LTS",
            "ALPHA" => " Alpha",
            "BETA" => " Beta",
            _ => string.Empty
        };

    private static string FormatBytes(long bytes)
    {
        var gigabytes = Math.Max(0, bytes) / 1024d / 1024d / 1024d;
        return gigabytes >= 0.1
            ? $"{gigabytes:0.##} GB"
            : $"{Math.Max(0, bytes) / 1024d / 1024d:0.#} MB";
    }
}

public sealed partial class InstallEditorDialog : ContentDialog
{
    private const int PageSize = 25;
    private readonly UnityEditorReleaseService _releaseService = new();
    private readonly HashSet<string> _installedVersions;
    private readonly List<UnityEditorRelease> _loadedReleases = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchCancellation;
    private string _selectedChannel = "official";
    private int _nextOffset;
    private int _total;
    private bool _isLoading;
    private bool _isUpdatingArchiveFilters;
    private readonly string? _initialVersionQuery;

    public ObservableCollection<UnityEditorReleaseListItem> Releases { get; } = [];
    public UnityEditorRelease? SelectedRelease { get; private set; }

    public InstallEditorDialog(
        IEnumerable<string> installedVersions,
        string? initialVersionQuery = null)
    {
        _installedVersions = installedVersions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _initialVersionQuery = string.IsNullOrWhiteSpace(initialVersionQuery)
            ? null
            : initialVersionQuery.Trim();
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialVersionQuery is null)
        {
            ReleaseSelector.SelectedItem = OfficialSelectorItem;
            return;
        }

        ArchiveSearchBox.Text = _initialVersionQuery;
        ReleaseSelector.SelectedItem = ArchiveSelectorItem;
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    private async void OnReleaseSelectorChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not FrameworkElement { Tag: string channel }
            || channel == _selectedChannel && _loadedReleases.Count > 0)
        {
            return;
        }

        _selectedChannel = channel;
        ArchiveFiltersPanel.Visibility =
            channel == "archive" ? Visibility.Visible : Visibility.Collapsed;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _loadCancellation?.Cancel();
        while (_isLoading)
        {
            await Task.Delay(10);
        }

        _nextOffset = 0;
        _total = 0;
        _loadedReleases.Clear();
        Releases.Clear();
        await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        if (_isLoading || (_total > 0 && _nextOffset >= _total))
        {
            return;
        }

        _isLoading = true;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        SetLoadingState(true);
        try
        {
            var isAppend = _nextOffset > 0;
            var page = await _releaseService.GetReleasesAsync(
                _nextOffset,
                PageSize,
                GetStreamFilter(),
                _selectedChannel == "archive" ? ArchiveSearchBox.Text : null,
                _loadCancellation.Token);
            _nextOffset = page.Offset + page.Limit;
            _total = page.Total;
            _loadedReleases.AddRange(page.Releases);
            UpdateArchiveVersionFilters();
            ApplyFilter(isAppend);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Releases.Clear();
            ShowError(ex.Message);
        }
        finally
        {
            _isLoading = false;
            SetLoadingState(false);
        }
    }

    private void ApplyFilter(bool append = false)
    {
        IEnumerable<UnityEditorRelease> filtered = _selectedChannel switch
        {
            "official" => SelectOfficialReleases(_loadedReleases),
            "prerelease" => SelectPreReleases(_loadedReleases),
            _ => ApplyArchiveFilters(_loadedReleases)
        };

        var visibleReleases = filtered.ToList();
        if (!append)
        {
            Releases.Clear();
        }

        var existingVersions = append
            ? Releases.Select(item => item.Release.Version)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (var release in visibleReleases)
        {
            if (existingVersions.Contains(release.Version))
            {
                continue;
            }

            Releases.Add(new UnityEditorReleaseListItem
            {
                Release = release,
                IsInstalled = _installedVersions.Contains(release.Version)
            });
        }

        EmptyPanel.Visibility =
            Releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReleasesListView.Visibility =
            Releases.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorInfoBar.IsOpen = false;
    }

    private IEnumerable<UnityEditorRelease> ApplyArchiveFilters(
        IEnumerable<UnityEditorRelease> releases)
    {
        var versionFamily = GetSelectedFilterTag(ArchiveVersionFilterComboBox);
        var stream = GetSelectedFilterTag(ArchiveStreamFilterComboBox);

        return releases.Where(release =>
            (string.IsNullOrWhiteSpace(versionFamily)
             || GetVersionFamily(release.Version).Equals(
                 versionFamily,
                 StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(stream)
                || release.Stream.Equals(stream, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateArchiveVersionFilters()
    {
        if (ArchiveVersionFilterComboBox is null)
        {
            return;
        }

        var selectedTag = GetSelectedFilterTag(ArchiveVersionFilterComboBox);
        var families = _loadedReleases
            .Select(release => GetVersionFamily(release.Version))
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(family => family, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _isUpdatingArchiveFilters = true;
        ArchiveVersionFilterComboBox.Items.Clear();
        ArchiveVersionFilterComboBox.Items.Add(
            new ComboBoxItem { Content = "All versions", Tag = string.Empty });
        foreach (var family in families)
        {
            ArchiveVersionFilterComboBox.Items.Add(
                new ComboBoxItem
                {
                    Content = family.StartsWith("Unity ", StringComparison.Ordinal)
                        ? family
                        : $"Unity {family}",
                    Tag = family
                });
        }

        ArchiveVersionFilterComboBox.SelectedItem =
            ArchiveVersionFilterComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    string.Equals(item.Tag as string, selectedTag, StringComparison.OrdinalIgnoreCase))
            ?? ArchiveVersionFilterComboBox.Items[0];
        _isUpdatingArchiveFilters = false;
    }

    private static string GetVersionFamily(string version)
    {
        var components = version.Split('.');
        if (components.Length == 0)
        {
            return version;
        }

        return components[0] == "6000"
            ? "Unity 6"
            : components[0];
    }

    private static string GetSelectedFilterTag(ComboBox comboBox)
        => comboBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;

    private void OnArchiveFilterSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isUpdatingArchiveFilters || _selectedChannel != "archive")
        {
            return;
        }

        ApplyFilter();
    }

    private static IEnumerable<UnityEditorRelease> SelectOfficialReleases(
        IEnumerable<UnityEditorRelease> releases)
    {
        var source = releases.ToList();
        var recommended = source
            .Where(release => release.IsRecommended)
            .OrderByDescending(release => release.ReleaseDate);
        var latestLtsLines = source
            .Where(release => release.Stream.Equals("LTS", StringComparison.OrdinalIgnoreCase))
            .GroupBy(release => GetReleaseLine(release.Version), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(release => release.ReleaseDate).First())
            .OrderByDescending(release => release.ReleaseDate);

        return recommended
            .Concat(latestLtsLines)
            .DistinctBy(release => release.Version, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<UnityEditorRelease> SelectPreReleases(
        IEnumerable<UnityEditorRelease> releases)
        => releases
            .Where(release =>
                release.Stream.Equals("ALPHA", StringComparison.OrdinalIgnoreCase)
                || release.Stream.Equals("BETA", StringComparison.OrdinalIgnoreCase))
            .GroupBy(release => release.Stream, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(release => release.ReleaseDate).First())
            .OrderByDescending(release => release.ReleaseDate);

    private static string GetReleaseLine(string version)
    {
        var components = version.Split('.');
        return components.Length >= 2 ? $"{components[0]}.{components[1]}" : version;
    }

    private IReadOnlyCollection<string>? GetStreamFilter()
        => _selectedChannel switch
        {
            "official" => ["SUPPORTED", "LTS"],
            "prerelease" => ["ALPHA", "BETA"],
            _ => null
        };

    private void SetLoadingState(bool isLoading)
    {
        LoadingPanel.Visibility =
            isLoading && Releases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (LoadingPanel.Children.OfType<ProgressRing>().FirstOrDefault() is { } ring)
        {
            ring.IsActive = LoadingPanel.Visibility == Visibility.Visible;
        }
        LoadingProgressBar.Visibility =
            isLoading && Releases.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            ErrorInfoBar.IsOpen = false;
        }
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        LoadingProgressBar.Visibility = Visibility.Collapsed;
        ReleasesListView.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    private async void OnArchiveSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput
            || _selectedChannel != "archive")
        {
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _searchCancellation.Token);
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnReleaseContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (_selectedChannel == "archive"
            && args.ItemIndex >= Releases.Count - 3
            && _nextOffset < _total)
        {
            await LoadPageAsync();
        }
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
        => await ReloadAsync();

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: UnityEditorRelease release })
        {
            return;
        }

        SelectedRelease = release;
        Hide();
    }

    private async void OnReleaseNotesClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Uri uri })
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private async void OnBetaProgramClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://unity.com/releases/editor/beta"));
}
