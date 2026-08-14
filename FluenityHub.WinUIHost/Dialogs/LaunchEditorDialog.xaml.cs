using FluenityHub_WinUIHost.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public enum LaunchEditorChoice
{
    Cancel,
    Sandbox,
    NewProject,
    RecentProject
}

public sealed partial class LaunchEditorDialog : ContentDialog
{
    public LaunchEditorChoice SelectedChoice { get; private set; } = LaunchEditorChoice.Cancel;
    public UnityProjectInfo? TargetRecentProject { get; private set; }

    public LaunchEditorDialog(UnityEditorInfo editor, UnityProjectInfo? matchingRecentProject = null)
    {
        InitializeComponent();
        Title = $"Launch Unity {editor.Version}";

        if (MainWindow.Instance is not null)
        {
            RequestedTheme = MainWindow.Instance.CurrentTheme;
        }

        if (matchingRecentProject is not null)
        {
            TargetRecentProject = matchingRecentProject;
            RecentProjectCard.Header = $"Open '{matchingRecentProject.Title}'";
            RecentProjectCard.Description = $"Last modified {matchingRecentProject.LastModifiedUtc.ToLocalTime():g}";
            RecentProjectCard.Visibility = Visibility.Visible;
        }
    }

    private void OnRecentProjectClick(object sender, RoutedEventArgs e)
    {
        SelectedChoice = LaunchEditorChoice.RecentProject;
        Hide();
    }

    private void OnSandboxClick(object sender, RoutedEventArgs e)
    {
        SelectedChoice = LaunchEditorChoice.Sandbox;
        Hide();
    }

    private void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        SelectedChoice = LaunchEditorChoice.NewProject;
        Hide();
    }
}
