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
        if (sender is MenuFlyoutItem item)
        {
            var flags = item.Tag as string;
            if (string.IsNullOrEmpty(flags) || flags.Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
            {
                ArgumentsTextBox.Text = string.Empty;
            }
            else
            {
                var currentText = ArgumentsTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(currentText))
                {
                    ArgumentsTextBox.Text = flags;
                }
                else if (!currentText.Contains(flags, StringComparison.OrdinalIgnoreCase))
                {
                    ArgumentsTextBox.Text = $"{currentText} {flags}";
                }
            }
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
            // Ignore shell errors
        }
    }
}
