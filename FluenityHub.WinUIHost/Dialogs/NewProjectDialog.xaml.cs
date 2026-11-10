using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class NewProjectDialog : ContentDialog
{
    private readonly CancellationTokenSource _editorLoad = new();
    private bool _configured;
    public string CreatedProjectPath => ProjectForm.CreatedProjectPath;
    public string CreatedProjectTitle => ProjectForm.CreatedProjectTitle;
    public string SelectedVersion => ProjectForm.SelectedVersion;

    public NewProjectDialog()
    {
        InitializeComponent();
        if (MainWindow.Instance is { } window) RequestedTheme = window.CurrentTheme;
        ProjectForm.StateChanged += OnFormStateChanged;
    }

    public NewProjectDialog(IReadOnlyDictionary<string, string> installedEditors,
        CustomTemplateInfo? selectedTemplate = null, string? initialVersion = null) : this()
    {
        ProjectForm.Configure(installedEditors, selectedTemplate, initialVersion);
        _configured = true;
        LoadingPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        ProjectForm.Visibility = Visibility.Visible;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_configured) return;
        _configured = true;
        var token = _editorLoad.Token;
        try
        {
            var editors = await Task.Run(() => new UnityEditorLocator().GetInstalledEditors(
                new AppSettingsStore().Load().CustomEditorPaths), token);
            token.ThrowIfCancellationRequested();
            if (editors.Count == 0)
            {
                EditorErrorInfoBar.Title = "No Unity Editor found";
                EditorErrorInfoBar.Message = "Install or locate a Unity Editor before creating a project.";
                EditorErrorInfoBar.IsOpen = true;
                return;
            }
            string? version = null;
            try { version = await Task.Run(() => UnityEditorPreferenceService.GetDefaultVersionAsync(token), token); }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                EditorErrorInfoBar.Title = "Default Editor unavailable";
                EditorErrorInfoBar.Message = "Choose an installed Editor to continue.";
                EditorErrorInfoBar.IsOpen = true;
            }
            token.ThrowIfCancellationRequested();
            ProjectForm.Configure(editors, initialVersion: version);
            ProjectForm.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!token.IsCancellationRequested)
            {
                EditorErrorInfoBar.Title = "Could not find Unity Editors";
                EditorErrorInfoBar.Message = ex.Message;
                EditorErrorInfoBar.IsOpen = true;
            }
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
        }
    }

    private void OnFormStateChanged(object? sender, EventArgs args)
    {
        IsPrimaryButtonEnabled = ProjectForm.CanCreate;
        CloseButtonText = ProjectForm.IsCanceling ? "Canceling..." :
            ProjectForm.IsCreating && !ProjectForm.CanCancelCreation ? "Working..." : "Cancel";
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { args.Cancel = !await ProjectForm.CreateAsync(); }
        finally { deferral.Complete(); }
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ProjectForm.IsCreating) return;
        args.Cancel = true;
        ProjectForm.CancelCreation();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!ProjectForm.IsCreating) return;
        args.Cancel = true;
        ProjectForm.CancelCreation();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _editorLoad.Cancel();
        _editorLoad.Dispose();
        ProjectForm.Close();
    }
}
