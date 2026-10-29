using System.Text.Json;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

internal static class ProjectDetailsService
{
    public static Task<ProjectDetailsInfo> ReadAsync(string projectPath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            long bytes = 0;
            var pending = new Stack<string>();
            if ((File.GetAttributes(projectPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Size is not calculated for a project folder that is a symbolic link or junction.");
            pending.Push(projectPath);
            while (pending.TryPop(out var path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo directory) pending.Push(directory.FullName);
                    else if (entry is FileInfo file) bytes = checked(bytes + file.Length);
                }
            }
            var pipeline = "Built-in or custom";
            var manifest = Path.Combine(projectPath, "Packages", "manifest.json");
            if (File.Exists(manifest))
            {
                using var document = ReadSmallJson(manifest);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("dependencies", out var dependencies)
                    && dependencies.ValueKind == JsonValueKind.Object)
                {
                    var urp = dependencies.TryGetProperty("com.unity.render-pipelines.universal", out _);
                    var hdrp = dependencies.TryGetProperty("com.unity.render-pipelines.high-definition", out _);
                    pipeline = (urp, hdrp) switch
                    {
                        (true, true) => "URP and HDRP packages installed",
                        (true, false) => "Universal Render Pipeline package installed",
                        (false, true) => "High Definition Render Pipeline package installed",
                        _ => pipeline
                    };
                }
            }
            var count = 0;
            var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "templatesSettings.json");
            if (File.Exists(settingsPath))
            {
                using var settings = ReadSmallJson(settingsPath);
                if (settings.RootElement.ValueKind == JsonValueKind.Object
                    && settings.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Object)
                    foreach (var source in sources.EnumerateObject())
                        if (source.Value.ValueKind == JsonValueKind.Object && source.Value.TryGetProperty("projectPath", out var origin)
                            && string.Equals(origin.GetString()?.TrimEnd('\\', '/'), projectPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) count++;
            }
            return new ProjectDetailsInfo(pipeline, bytes, count);
        }, cancellationToken);

    private static JsonDocument ReadSmallJson(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Project metadata is too large to read.");
        return JsonDocument.Parse(stream);
    }
}
