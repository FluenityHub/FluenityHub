using System;
using System.Collections.Generic;
using System.Linq;
using FluenityHub_WinUIHost.Models;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluenityHub_WinUIHost.Helpers;

public sealed record TagColorPreset(string Key, string Name, string HexColor, Color Color);

public static class TagColorHelper
{
    private static readonly object SyncRoot = new();
    private static Dictionary<string, string>? _cachedTagColors;

    public static readonly IReadOnlyList<TagColorPreset> Presets = new List<TagColorPreset>
    {
        new("Blue", "Blue", "#0078D4", Color.FromArgb(255, 0, 120, 212)),
        new("Teal", "Teal", "#008272", Color.FromArgb(255, 0, 130, 114)),
        new("Green", "Green", "#107C41", Color.FromArgb(255, 16, 124, 65)),
        new("Yellow", "Yellow", "#D8A000", Color.FromArgb(255, 216, 160, 0)),
        new("Orange", "Orange", "#DA3B01", Color.FromArgb(255, 218, 59, 1)),
        new("Red", "Red", "#E81123", Color.FromArgb(255, 232, 17, 35)),
        new("Purple", "Purple", "#8764B8", Color.FromArgb(255, 135, 100, 184)),
        new("Pink", "Pink", "#E3008C", Color.FromArgb(255, 227, 0, 140))
    };

    public static TagColorPreset DefaultPreset => Presets[0];

    public static Color ParseColor(string? keyOrHex)
    {
        if (string.IsNullOrWhiteSpace(keyOrHex)) return DefaultPreset.Color;

        // 1. Check if key is a preset
        var preset = Presets.FirstOrDefault(p => p.Key.Equals(keyOrHex, StringComparison.OrdinalIgnoreCase));
        if (preset != null) return preset.Color;

        // 2. Try parsing as Hex string (e.g. "#FF4500" or "FF4500")
        try
        {
            var hex = keyOrHex.Trim().TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            else if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            // Fallback
        }

        return DefaultPreset.Color;
    }

    private static readonly Dictionary<string, string> DefaultUnityTagColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Game"] = "Blue",
        ["Client Project"] = "Teal",
        ["Prototype"] = "Purple",
        ["Personal"] = "Pink",
        ["Simulation"] = "Orange",
        ["Archived"] = "Red",
        ["Visualization"] = "Green",
        ["Work in Progress"] = "Yellow",
        ["2D"] = "Blue",
        ["3D"] = "Teal"
    };

    public static Color GetColorForTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return DefaultPreset.Color;

        var normalizedTag = tag.Trim();
        var tagColors = GetCachedTagColors();
        if (tagColors.TryGetValue(normalizedTag, out var key))
        {
            return ParseColor(key);
        }

        if (DefaultUnityTagColors.TryGetValue(normalizedTag, out var presetKey))
        {
            return ParseColor(presetKey);
        }

        // String.GetHashCode is randomized between processes. FNV-1a keeps an
        // unconfigured tag on the same palette entry across app restarts.
        var hash = GetStableHash(normalizedTag);
        return Presets[(int)(hash % (uint)Presets.Count)].Color;
    }

    public static SolidColorBrush GetSolidBrushForTag(string tag)
    {
        var c = GetColorForTag(tag);
        return new SolidColorBrush(c);
    }

    public static SolidColorBrush GetTintBrushForTag(string tag, double opacity = 0.16)
    {
        var c = GetColorForTag(tag);
        return new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B));
    }

    /// <summary>
    /// Returns a 100% opaque (Alpha=255) tinted brush specifically calibrated for floating template badges
    /// over cover media. Never transparent, completely blocks underlying artwork.
    /// </summary>
    public static SolidColorBrush GetBadgeBackgroundBrushForTag(string tag)
    {
        var c = GetColorForTag(tag);
        byte r = (byte)Math.Clamp((int)(c.R * 0.65 + 32 * 0.35), 0, 255);
        byte g = (byte)Math.Clamp((int)(c.G * 0.65 + 32 * 0.35), 0, 255);
        byte b = (byte)Math.Clamp((int)(c.B * 0.65 + 32 * 0.35), 0, 255);
        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
    }

    public static void SetTagColor(string tag, string colorKeyOrHex)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;

        var cleanTag = tag.Trim();
        var cleanValue = colorKeyOrHex.Trim();

        lock (SyncRoot)
        {
            var store = new AppSettingsStore();
            var settings = store.Load();
            settings.TagColors = new Dictionary<string, string>(
                settings.TagColors ?? [],
                StringComparer.OrdinalIgnoreCase)
            {
                [cleanTag] = cleanValue
            };
            store.Save(settings);

            _cachedTagColors ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _cachedTagColors[cleanTag] = cleanValue;
        }
    }

    private static Dictionary<string, string> GetCachedTagColors()
    {
        lock (SyncRoot)
        {
            if (_cachedTagColors is not null)
            {
                return _cachedTagColors;
            }

            var settings = new AppSettingsStore().Load();
            _cachedTagColors = new Dictionary<string, string>(
                settings.TagColors ?? [],
                StringComparer.OrdinalIgnoreCase);
            return _cachedTagColors;
        }
    }

    private static uint GetStableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var character in value)
        {
            hash ^= char.ToLowerInvariant(character);
            hash *= prime;
        }

        return hash;
    }
}
