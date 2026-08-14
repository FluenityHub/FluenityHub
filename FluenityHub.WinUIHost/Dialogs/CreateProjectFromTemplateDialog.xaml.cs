using System;
using System.IO;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class CreateProjectFromTemplateDialog : ContentDialog
{
    public string ProjectName => ProjectNameTextBox.Text.Trim();
    public string LocationPath => LocationTextBox.Text.Trim();

    public CreateProjectFromTemplateDialog(CustomTemplateInfo template)
    {
        InitializeComponent();

        RequestedTheme = MainWindow.Instance?.CurrentTheme ?? ElementTheme.Default;
        ProjectNameTextBox.Text = $"{template.Name} Project";
        LocationTextBox.Text =
            new UnityHubProjectSettingsService().GetProjectLocation();
        TemplateInfoBar.Title = template.Name;
        TemplateInfoBar.Message = $"Template {template.Version} · Unity {template.EditorVersion}";
        ValidateInput(showMessage: false);
    }



    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        ValidateInput(showMessage: false);
    }

    private async void OnBrowseLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (XamlRoot is null)
            {
                return;
            }

            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(
                XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                Title = "Choose a project location",
                CommitButtonText = "Select folder",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List
            };

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                LocationTextBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            ValidationInfoBar.Message = $"The folder picker could not be opened: {ex.Message}";
            ValidationInfoBar.IsOpen = true;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ValidateInput(showMessage: true))
        {
            args.Cancel = true;
        }
    }

    private bool ValidateInput(bool showMessage)
    {
        var name = ProjectNameTextBox.Text.Trim();
        var location = LocationTextBox.Text.Trim();
        string? error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a project name.";
        }
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "The project name contains characters that cannot be used in a folder name.";
        }
        else if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "The project name cannot end with a space or period.";
        }
        else if (string.IsNullOrWhiteSpace(location))
        {
            error = "Choose a location folder.";
        }
        else if (Directory.Exists(Path.Combine(location, name)))
        {
            error = "A folder with this project name already exists at the selected location.";
        }

        IsPrimaryButtonEnabled = error is null;
        ValidationInfoBar.Message = error ?? string.Empty;
        ValidationInfoBar.IsOpen = showMessage && error is not null;
        return error is null;
    }
}
