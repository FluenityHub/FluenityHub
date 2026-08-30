using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluenityHub_WinUIHost.Models;
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
    public CustomTemplateInfo TemplateInfo { get; }
    public string EditorVersionDisplay { get; }
    public string CreatedAtDisplay { get; }
    public string SizeDisplay { get; }
    public string CategoryDisplay { get; }
    public string TagsDisplay { get; }

    public TemplatePropertiesDialog(CustomTemplateInfo template)
    {
        TemplateInfo = template;
        EditorVersionDisplay = string.IsNullOrWhiteSpace(template.EditorVersion)
            ? "Not specified"
            : template.EditorVersion;
        CreatedAtDisplay = template.CreatedAt.ToString("g");
        SizeDisplay = FormatFileSize(GetContentSize(template));
        CategoryDisplay = template.Id.StartsWith("com.unity.template.", StringComparison.OrdinalIgnoreCase)
            ? "Unity"
            : "Custom";
        TagsDisplay = template.Tags.Count == 0
            ? "None"
            : string.Join(", ", template.Tags);

        InitializeComponent();

        var packages = ReadPackages(template.TemplateFolderPath);
        PackagesListView.ItemsSource = packages;
        PackagesTable.Visibility = packages.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        NoPackagesTextBlock.Visibility = packages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenLocationButton.IsEnabled = Directory.Exists(template.TemplateFolderPath);
    }

    private void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(TemplateInfo.TemplateFolderPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = TemplateInfo.TemplateFolderPath,
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
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Template package metadata could not be read: {ex.Message}");
            return [];
        }
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