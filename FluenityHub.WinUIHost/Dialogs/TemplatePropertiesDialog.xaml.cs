using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Dialogs;

public sealed class TemplatePackageInfo
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
}

public sealed partial class TemplatePropertiesDialog : ContentDialog
{
    public string TemplateName { get; }
    public string TemplateVersion { get; }
    public string TemplateDescription { get; }
    public string LocationDisplay { get; }
    public string DetailsLabel { get; }
    public string EditorVersionDisplay { get; }
    public string CreatedAtDisplay { get; }
    public string SizeDisplay { get; }
    public string CategoryDisplay { get; }
    public string DetailsDisplay { get; }

    public TemplatePropertiesDialog(CustomTemplateInfo template)
    {
        TemplateName = template.Name;
        TemplateVersion = template.DisplayVersion;
        TemplateDescription = template.DisplayDescription;
        LocationDisplay = template.TemplateFolderPath;
        DetailsLabel = "Tags";
        EditorVersionDisplay = string.IsNullOrWhiteSpace(template.EditorVersion)
            ? "Not specified"
            : template.EditorVersion;
        CreatedAtDisplay = template.CreatedAt.ToString("g");
        SizeDisplay = FormatFileSize(GetContentSize(template));
        CategoryDisplay = template.Id.StartsWith("com.unity.template.", StringComparison.OrdinalIgnoreCase)
            ? "Unity"
            : "Custom";
        DetailsDisplay = template.Tags.Count == 0
            ? "None"
            : string.Join(", ", template.Tags);

        InitializeComponent();

        SetPackages(ReadPackages(template.TemplateFolderPath));
        OpenLocationButton.IsEnabled = Directory.Exists(LocationDisplay);
    }

    internal TemplatePropertiesDialog(UnityProjectTemplateItem item, string editorVersion)
    {
        var template = item.Template;
        TemplateName = template.Name;
        TemplateVersion = template.HasVersion ? template.Version : "Not specified";
        TemplateDescription = string.IsNullOrWhiteSpace(template.Description) ? "No description provided." : template.Description;
        EditorVersionDisplay = editorVersion;
        CreatedAtDisplay = string.Empty;
        CategoryDisplay = template.CardCategory;
        DetailsLabel = "Availability";
        DetailsDisplay = template.ArchivePath is not null ? "Included with the Editor" :
            item.IsDownloaded ? "Downloaded" : item.IsDownloading ? "Downloading" : "Not downloaded";
        LocationDisplay = template.ArchivePath ?? (item.CanRemoveDownload
            ? UnityTemplateCatalogService.GetArchiveCachePath(template, item.DownloadDirectory) : string.Empty);
        SizeDisplay = FormatFileSize(template.ArchivePath is { } path && File.Exists(path)
            ? new FileInfo(path).Length : template.DownloadSize);
        InitializeComponent();

        CreatedAtPanel.Visibility = Visibility.Collapsed;
        LocationPanel.Visibility = LocationDisplay.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenLocationButton.IsEnabled = LocationDisplay.Length > 0;
        if (LocationDisplay.Length == 0)
        {
            SetPackages([], template.DownloadUri is null ? "This template has no associated packages." :
                "Download the template to view its packages.");
            return;
        }

        SetPackages([], "Reading packages...");
        Loaded += ReadArchivePackages;
    }

    private async void ReadArchivePackages(object sender, RoutedEventArgs args)
    {
        Loaded -= ReadArchivePackages;
        using var request = new CancellationTokenSource();
        void CancelRead(ContentDialog dialog, ContentDialogClosedEventArgs closedArgs) => request.Cancel();
        Closed += CancelRead;
        try
        {
            var metadata = await Task.Run(() =>
                UnityTemplateCatalogService.ReadArchiveMetadataAsync(LocationDisplay, request.Token), request.Token);
            if (!request.IsCancellationRequested)
                SetPackages(metadata is { } document ? ReadDependencies(document) : []);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
        {
            Debug.WriteLine($"Template package metadata could not be read: {ex.GetType().Name}");
            if (!request.IsCancellationRequested)
                SetPackages([], "Package details couldn't be read from this archive.");
        }
        finally { Closed -= CancelRead; }
    }

    private void SetPackages(IReadOnlyList<TemplatePackageInfo> packages, string emptyMessage = "This template has no associated packages.")
    {
        PackagesListView.ItemsSource = packages;
        PackagesTable.Visibility = packages.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        NoPackagesTextBlock.Visibility = packages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoPackagesTextBlock.Text = emptyMessage;
    }

    private void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        var folder = File.Exists(LocationDisplay) ? Path.GetDirectoryName(LocationDisplay) : LocationDisplay;
        if (!Directory.Exists(folder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private static IReadOnlyList<TemplatePackageInfo> ReadPackages(string templateFolderPath)
    {
        try
        {
            var packageJsonPath = Path.Combine(templateFolderPath, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return ReadDependencies(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Template package metadata could not be read: {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<TemplatePackageInfo> ReadDependencies(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object)
            return [];

        return dependencies.EnumerateObject()
            .Select(dependency => new TemplatePackageInfo
            {
                Name = dependency.Name,
                Version = dependency.Value.ValueKind == JsonValueKind.String
                    ? dependency.Value.GetString() ?? string.Empty
                    : dependency.Value.ToString()
            })
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static long GetContentSize(CustomTemplateInfo template)
    {
        try
        {
            if (File.Exists(template.TarballPath))
            {
                return new FileInfo(template.TarballPath).Length;
            }

            return Directory.Exists(template.TemplateFolderPath)
                ? Directory.EnumerateFiles(template.TemplateFolderPath, "*", SearchOption.TopDirectoryOnly)
                    .Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Template size could not be read: {ex.Message}");
            return 0;
        }
    }

    private static string FormatFileSize(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return "Unknown";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)sizeBytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{sizeBytes:N0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }
}
