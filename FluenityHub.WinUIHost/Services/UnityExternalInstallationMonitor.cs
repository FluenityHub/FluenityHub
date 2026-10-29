using System.Text;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityExternalInstallationMonitor
{
    public static string ReadFingerprint(IEnumerable<UnityEditorInfo> editors, string installRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { installRoot };
        foreach (var editor in editors)
        {
            paths.Add(editor.InstallDirectory);
            var parent = Directory.GetParent(editor.InstallDirectory)?.FullName;
            if (parent is not null) paths.Add(parent);
            var playback = Path.Combine(editor.InstallDirectory, "Editor", "Data", "PlaybackEngines");
            paths.Add(playback);
            foreach (var child in new[] { "AndroidPlayer", "AndroidPlayer/SDK", "AndroidPlayer/NDK", "AndroidPlayer/OpenJDK" })
                paths.Add(Path.Combine(playback, child));
        }
        var signature = new StringBuilder();
        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = new DirectoryInfo(path);
                signature.Append(path).Append(':').Append(directory.Exists ? directory.LastWriteTimeUtc.Ticks : 0).Append(';');
                if (directory.Exists)
                    foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                        signature.Append(child.Name).Append(':').Append(child.LastWriteTimeUtc.Ticks).Append(';');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                signature.Append(path).Append(":unavailable;");
            }
        }
        return signature.ToString();
    }
}
