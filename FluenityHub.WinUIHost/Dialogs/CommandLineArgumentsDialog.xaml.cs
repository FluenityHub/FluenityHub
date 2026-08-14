using System;
using System.Diagnostics;
using FluenityHub_WinUIHost.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed partial class CommandLineArgumentsDialog : ContentDialog
{
    public string SavedArguments { get; private set; } = string.Empty;

    public CommandLineArgumentsDialog(UnityProjectInfo project)
    {
        InitializeComponent();

        if (project is not null)
        {
            ProjectTitleTextBlock.Text = project.Title;
            EditorVersionTextBlock.Text = project.Version;
            ArgumentsTextBox.Text = project.CommandLineArguments ?? string.Empty;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SavedArguments = ArgumentsTextBox.Text?.Trim() ?? string.Empty;
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string flags)
        {
            ArgumentsTextBox.Text = flags;
        }
    }

    private void OnDocLinkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://docs.unity3d.com/Manual/CommandLineArguments.html",
                UseShellExecute = true
            });
        }
        catch
        {
            // Opening documentation is optional; keep the dialog usable if it fails.
        }
    }
}
