using System.Collections.ObjectModel;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed class EditorVersionChoice
{
    public string Version { get; set; } = string.Empty;
    public bool RequiresInstallation { get; set; }
    public string DisplayName => $"Unity {Version}";
    public string Description => RequiresInstallation
        ? "Required by this project (Not installed)"
        : "Installed";
    public string BadgeText => GetBadgeText(Version);
    public Visibility BadgeVisibility => string.IsNullOrWhiteSpace(BadgeText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public override string ToString() => Version;

    private static string GetBadgeText(string version)
    {
        if (version.Contains("a", StringComparison.OrdinalIgnoreCase)) return "Alpha";
        if (version.Contains("b", StringComparison.OrdinalIgnoreCase)) return "Beta";
        if (version.StartsWith("2022.3", StringComparison.OrdinalIgnoreCase)
            || version.StartsWith("6000.0", StringComparison.OrdinalIgnoreCase)
            || version.StartsWith("6000.3", StringComparison.OrdinalIgnoreCase))
        {
            return "LTS";
        }

        return string.Empty;
    }
}

public sealed partial class MissingEditorVersionDialog : ContentDialog
{
    private readonly IReadOnlyDictionary<string, string> _installedEditors;
    private readonly UnityEditorLocator _editorLocator = new();
    private List<TargetPlatformInfo> _currentPlatforms = [];
    private bool _isSynchronizingSelection;

    public ObservableCollection<EditorVersionChoice> MissingVersionChoices { get; } = [];
    public ObservableCollection<EditorVersionChoice> InstalledVersionChoices { get; } = [];
    public string WarningMessage { get; }
    public EditorVersionChoice? SelectedChoice =>
        MissingVersionListView.SelectedItem as EditorVersionChoice
        ?? InstalledVersionsListView.SelectedItem as EditorVersionChoice;
    public bool InstallOtherVersionRequested { get; private set; }

    public string? SelectedTargetPlatform
    {
        get
        {
            var index = TargetPlatformComboBox.SelectedIndex - 1;
            return index >= 0 && index < _currentPlatforms.Count
                ? _currentPlatforms[index].Id
                : null;
        }
    }

    public MissingEditorVersionDialog(
        UnityProjectInfo project,
        IReadOnlyDictionary<string, string> installedEditors)
    {
        _installedEditors = installedEditors;
        WarningMessage =
            $"To open this project, install Unity {project.Version} or select a different installed version below. " +
            "Opening it with another Editor version may upgrade the project and may not work as expected.";

        MissingVersionChoices.Add(new EditorVersionChoice
        {
            Version = project.Version,
            RequiresInstallation = true
        });

        var installedChoices = installedEditors.Keys
            .Where(version => !version.Equals(project.Version, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(version => version, UnityVersionStringComparer.Instance)
            .Select(version => new EditorVersionChoice
            {
                Version = version,
                RequiresInstallation = false
            })
            .ToArray();
        foreach (var choice in installedChoices)
        {
            InstalledVersionChoices.Add(choice);
        }

        InitializeComponent();
        Title = $"{project.Title}: Editor required";
        InstalledVersionsPanel.Visibility = InstalledVersionChoices.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MissingVersionListView.SelectedItem = MissingVersionChoices[0];
        UpdateSelectionState();
    }

    private void OnMissingVersionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        if (MissingVersionListView.SelectedItem is not null)
        {
            _isSynchronizingSelection = true;
            InstalledVersionsListView.SelectedItem = null;
            _isSynchronizingSelection = false;
        }

        UpdateSelectionState();
    }

    private void OnInstalledVersionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        if (InstalledVersionsListView.SelectedItem is not null)
        {
            _isSynchronizingSelection = true;
            MissingVersionListView.SelectedItem = null;
            _isSynchronizingSelection = false;
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var selection = SelectedChoice;
        IsPrimaryButtonEnabled = selection is not null;
        if (selection is null)
        {
            PrimaryButtonText = "Continue";
            TargetPlatformComboBox.Visibility = Visibility.Collapsed;
            return;
        }

        PrimaryButtonText = selection.RequiresInstallation
            ? "Install"
            : "Open";
        TargetPlatformComboBox.Visibility = selection.RequiresInstallation
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateTargetPlatforms(selection);
    }

    private void UpdateTargetPlatforms(EditorVersionChoice selection)
    {
        if (selection.RequiresInstallation)
        {
            _currentPlatforms = [];
            TargetPlatformComboBox.ItemsSource = null;
            return;
        }

        var executable = _editorLocator.FindEditorExecutable(selection.Version, _installedEditors);
        _currentPlatforms = !string.IsNullOrWhiteSpace(executable)
            ? _editorLocator.GetInstalledTargetPlatforms(executable)
            : [];
        var choices = new List<string> { "Default from project settings" };
        choices.AddRange(_currentPlatforms.Select(platform => platform.DisplayName));
        TargetPlatformComboBox.ItemsSource = choices;
        TargetPlatformComboBox.SelectedIndex = 0;
    }

    private void OnInstallOtherVersionClick(object sender, RoutedEventArgs e)
    {
        InstallOtherVersionRequested = true;
        Hide();
    }
}

internal sealed class UnityVersionStringComparer : IComparer<string>
{
    public static UnityVersionStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var left = UnityVersion.Parse(x);
        var right = UnityVersion.Parse(y);
        var numericComparison = left.CompareTo(right);
        return numericComparison != 0
            ? numericComparison
            : StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    private readonly record struct UnityVersion(int Major, int Minor, int Patch, int Stage, int Revision)
        : IComparable<UnityVersion>
    {
        public int CompareTo(UnityVersion other)
        {
            var comparison = Major.CompareTo(other.Major);
            if (comparison != 0) return comparison;
            comparison = Minor.CompareTo(other.Minor);
            if (comparison != 0) return comparison;
            comparison = Patch.CompareTo(other.Patch);
            if (comparison != 0) return comparison;
            comparison = Stage.CompareTo(other.Stage);
            return comparison != 0 ? comparison : Revision.CompareTo(other.Revision);
        }

        public static UnityVersion Parse(string value)
        {
            var numeric = new int[5];
            var slot = 0;
            var current = 0;
            var hasDigits = false;
            foreach (var character in value)
            {
                if (char.IsDigit(character))
                {
                    current = checked((current * 10) + (character - '0'));
                    hasDigits = true;
                }
                else if (hasDigits && slot < numeric.Length)
                {
                    numeric[slot++] = current;
                    current = 0;
                    hasDigits = false;
                }
            }

            if (hasDigits && slot < numeric.Length)
            {
                numeric[slot] = current;
            }

            return new UnityVersion(numeric[0], numeric[1], numeric[2], numeric[3], numeric[4]);
        }
    }
}
