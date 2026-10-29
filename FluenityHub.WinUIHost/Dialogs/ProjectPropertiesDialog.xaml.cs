using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class ProjectPropertiesDialog : ContentDialog
{
    public UnityProjectInfo Project { get; }
    public string Tags => Project.Tags.Count == 0 ? "None" : string.Join(", ", Project.Tags);
    public string SourceControl => string.IsNullOrWhiteSpace(Project.SourceControlProvider) ? "Not connected" : $"{Project.SourceControlProvider}: {Project.GitBranch ?? Project.SourceControlDetail}";
    private readonly CancellationTokenSource _cancellation = new();
    public ProjectPropertiesDialog(UnityProjectInfo project)
    {
        Project = project;
        InitializeComponent();
        RemoteSizeButton.IsEnabled = !string.IsNullOrWhiteSpace(project.SourceControlRemoteUrl);
        Opened += OnOpened;
        Closed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); };
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            var details = await ProjectDetailsService.ReadAsync(Project.Path, _cancellation.Token);
            DetailsText.Text = $"Render pipeline: {details.RenderPipeline}\nSize on disk: {details.SizeBytes / 1073741824d:0.##} GiB\nCustom templates: {details.TemplateCount}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DetailsText.Text = $"Project details could not be read: {ex.Message}"; }
        finally { DetailsProgress.IsActive = false; DetailsProgress.Visibility = Visibility.Collapsed; }
    }

    private async void OnReleaseNotes(object sender, RoutedEventArgs args)
    {
        try
        {
            var release = await new UnityEditorReleaseService().GetReleaseAsync(Project.Version, _cancellation.Token);
            if (release?.ReleaseNotesUri is { } uri) await Windows.System.Launcher.LaunchUriAsync(uri);
            else DetailsText.Text = "Release notes are not available for this Editor build.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { DetailsText.Text = ex.Message; }
    }

    private async void OnRemoteSize(object sender, RoutedEventArgs args)
    {
        RemoteSizeButton.IsEnabled = false;
        RemoteSizeText.Text = "Checking repository size...";
        try { RemoteSizeText.Text = await RemoteRepositoryInfoService.ReadSizeAsync(Project.SourceControlRemoteUrl, _cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RemoteSizeText.Text = $"Repository size could not be read: {ex.Message}"; }
        finally { RemoteSizeButton.IsEnabled = true; }
    }
}
