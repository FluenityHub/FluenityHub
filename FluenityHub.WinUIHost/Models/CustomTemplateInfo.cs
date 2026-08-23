using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using FluenityHub_WinUIHost.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace FluenityHub_WinUIHost.Models;

public sealed class TemplateTagBadgeViewModel
{
    public string Name { get; }
    public SolidColorBrush FillBrush { get; }
    public SolidColorBrush DotBrush { get; }

    public TemplateTagBadgeViewModel(string name)
    {
        Name = name;
        DotBrush = TagColorHelper.GetSolidBrushForTag(name);
        FillBrush = TagColorHelper.GetBadgeBackgroundBrushForTag(name);
    }
}

public sealed class CustomTemplateInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string EditorVersion { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool KeepProjectSettings { get; set; } = true;
    public List<string> IncludedRootFiles { get; set; } = [];
    public bool HasProjectNamePlaceholder { get; set; }
    public string TemplateFolderPath { get; set; } = string.Empty;
    public string TarballPath { get; set; } = string.Empty;
    public bool IsUnityHubTemplate { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool HasTags => Tags is { Count: > 0 };

    [JsonIgnore]
    public string PrimaryTag => HasTags ? Tags[0] : string.Empty;

    [JsonIgnore]
    public string TagBadgeLabel => Tags?.Count switch
    {
        null or 0 => string.Empty,
        1 => Tags[0],
        _ => $"{Tags[0]} +{Tags.Count - 1}"
    };

    [JsonIgnore]
    public SolidColorBrush PrimaryTagColorBrush => TagColorHelper.GetSolidBrushForTag(PrimaryTag);

    [JsonIgnore]
    public SolidColorBrush PrimaryTagBadgeBrush => TagColorHelper.GetBadgeBackgroundBrushForTag(PrimaryTag);

    [JsonIgnore]
    public string TagsToolTip => HasTags
        ? $"Tags: {string.Join(", ", Tags)}"
        : "No tags set";

    [JsonIgnore]
    public bool IsEditorInstalled { get; set; } = true;

    [JsonIgnore]
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version;

    [JsonIgnore]
    public string DisplayDescription => string.IsNullOrWhiteSpace(Description) ? "No description provided." : Description;

    [JsonIgnore]
    public bool IsEditorMissing => !IsEditorInstalled;

    [JsonIgnore]
    public string EditorStatusLabel => IsEditorInstalled
        ? "Unity Editor installed"
        : "Unity Editor missing";

    [JsonIgnore]
    public string CreateButtonToolTip => IsEditorInstalled
        ? "Create project from template"
        : "This Editor version is not installed. Install it to create a project from this template.";

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrWhiteSpace(ImagePath) && File.Exists(ImagePath);

    [JsonIgnore]
    public Brush PreviewBackgroundBrush
    {
        get
        {
            ReadOnlySpan<uint> colors =
            [
                0xFFD13415,
                0xFF107D98,
                0xFFCC4E00,
                0xFF6E56CF,
                0xFF008573,
                0xFF218358,
                0xFFAB4ABA
            ];

            var seed = string.IsNullOrEmpty(Name) ? Id : Name;
            var index = string.IsNullOrEmpty(seed) ? 0 : seed[^1] % colors.Length;
            var value = colors[index];
            return new SolidColorBrush(Color.FromArgb(
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value));
        }
    }

    [JsonIgnore]
    public ImageSource? CoverImageSource
    {
        get
        {
            try
            {
                if (HasImage)
                {
                    return CreateCoverImageSource(ImagePath);
                }
            }
            catch
            {
            }
            return null;
        }
    }

    /// <summary>
    /// Loads a template cover that may be replaced in place by the edit dialog
    /// or by Unity Hub. Local file URIs are cached by WinUI by default, so the
    /// cache must be bypassed to display the replacement immediately.
    /// </summary>
    public static BitmapImage CreateCoverImageSource(string imagePath)
    {
        var image = new BitmapImage
        {
            CreateOptions = BitmapCreateOptions.IgnoreImageCache
        };
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        return image;
    }
}
