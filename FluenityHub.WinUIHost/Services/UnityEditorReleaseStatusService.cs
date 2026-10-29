using System.Diagnostics;
using System.Text.Json;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityEditorReleaseStatusService
{
    internal static Dictionary<string, string> ReadCachedStreams(
        IEnumerable<UnityEditorInfo> editors,
        string? hubDataDirectory = null)
    {
        var installed = editors.ToArray();
        var versions = installed.Select(editor => editor.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var streams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (installed.Length == 0) return streams;
        var hubDirectory = hubDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");

        foreach (var file in new[] { "downloadable-editors-cache.json", "live-platform-releases-cache.json", "archive-cache.json" })
        {
            ReadFile(Path.Combine(hubDirectory, "hub-gpui", file), root => ReadReleases(root, versions, streams));
            if (streams.Count == versions.Count) break;
        }

        foreach (var editor in installed.Where(editor => !streams.ContainsKey(editor.Version)))
        {
            ReadFile(Path.Combine(editor.InstallDirectory, "metadata.hub.json"), root =>
            {
                if (root.ValueKind != JsonValueKind.Object) return;
                var stream = root.TryGetProperty("releaseStream", out var value) && value.ValueKind == JsonValueKind.String
                    ? NormalizeStream(value.GetString()) : string.Empty;
                if (stream.Length == 0 && root.TryGetProperty("isLTS", out var isLts) && isLts.ValueKind == JsonValueKind.True)
                    stream = "LTS";
                if (stream.Length > 0) streams[editor.Version] = stream;
            });
        }
        return streams;
    }

    private static void ReadFile(string path, Action<JsonElement> read)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) return;
            using var input = File.OpenRead(path);
            using var document = JsonDocument.Parse(input);
            read(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"Editor release metadata unavailable in {Path.GetFileName(path)}: {ex.GetType().Name}");
        }
    }

    private static void ReadReleases(JsonElement value, HashSet<string> versions, Dictionary<string, string> streams)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ReadReleases(item, versions, streams);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && value.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.String)
            {
                var releaseVersion = version.GetString();
                var releaseStream = NormalizeStream(stream.GetString());
                if (releaseVersion is not null && versions.Contains(releaseVersion) && releaseStream.Length > 0)
                    streams.TryAdd(releaseVersion, releaseStream);
                return;
            }
            foreach (var property in value.EnumerateObject()) ReadReleases(property.Value, versions, streams);
        }
    }

    private static string NormalizeStream(string? stream) => stream?.ToUpperInvariant() switch
    {
        "LTS" => "LTS",
        "SUPPORTED" => "SUPPORTED",
        "TECH" => "TECH",
        "ALPHA" => "ALPHA",
        "BETA" => "BETA",
        _ => string.Empty
    };
}
