using System.Text;
using FluenityHub_WinUIHost.Helpers;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityEditorLaunchDiagnostics
{
    private static readonly object SyncRoot = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluenityHub",
        "Logs",
        "EditorLaunch",
        "latest.log");

    public static void Begin(string editorExecutable, string projectPath)
    {
        lock (SyncRoot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.WriteAllText(
                    LogPath,
                    $"FluenityHub Unity Editor launch diagnostics{Environment.NewLine}" +
                    $"Started: {DateTimeOffset.Now:O}{Environment.NewLine}" +
                    $"Editor: {editorExecutable}{Environment.NewLine}" +
                    $"Project: {projectPath}{Environment.NewLine}" +
                    $"Process: {Environment.ProcessId}{Environment.NewLine}" +
                    new string('-', 72) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never prevent an Editor launch.
            }
        }
    }

    public static void Write(string area, string message)
    {
        lock (SyncRoot)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.Now:O}] [{area}] {SensitiveDataRedactor.Redact(message)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never alter application behavior.
            }
        }
    }
}
