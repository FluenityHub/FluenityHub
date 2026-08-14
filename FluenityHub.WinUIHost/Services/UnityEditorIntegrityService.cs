using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record EditorIntegrityReport(
    string Version,
    string InstallPath,
    string ResolvedExePath,
    bool IsExecutableValid,
    bool IsMonoValid,
    bool IsPlaybackEnginesValid,
    List<string> MissingOrCorruptedItems,
    List<string> DiagnosticLogs
)
{
    public bool IsHealthy => IsExecutableValid && IsMonoValid && IsPlaybackEnginesValid && MissingOrCorruptedItems.Count == 0;
}

public class UnityEditorIntegrityService
{
    public Task<EditorIntegrityReport> CheckIntegrityAsync(UnityEditorInfo editor)
    {
        return Task.Run(() =>
        {
            var missingItems = new List<string>();
            var logs = new List<string>();

            if (string.IsNullOrWhiteSpace(editor.InstallDirectory) || !Directory.Exists(editor.InstallDirectory))
            {
                missingItems.Add($"Installation directory does not exist: {editor.InstallDirectory}");
                logs.Add($"[FAIL] Root folder missing: {editor.InstallDirectory}");
                return new EditorIntegrityReport(
                    editor.Version,
                    editor.InstallDirectory ?? string.Empty,
                    ResolvedExePath: string.Empty,
                    IsExecutableValid: false,
                    IsMonoValid: false,
                    IsPlaybackEnginesValid: false,
                    MissingOrCorruptedItems: missingItems,
                    DiagnosticLogs: logs
                );
            }

            // Resolve effective base directory (checking root and subfolder 'Editor/')
            string baseDir = editor.InstallDirectory;
            string unityExePath = Path.Combine(baseDir, "Unity.exe");

            if (!File.Exists(unityExePath))
            {
                var nestedEditorExe = Path.Combine(baseDir, "Editor", "Unity.exe");
                if (File.Exists(nestedEditorExe))
                {
                    baseDir = Path.Combine(baseDir, "Editor");
                    unityExePath = nestedEditorExe;
                    logs.Add($"[INFO] Detected Unity Editor subfolder layout: {baseDir}");
                }
                else
                {
                    // Deep search up to 2 subfolder levels for Unity.exe
                    try
                    {
                        var foundExe = Directory.GetFiles(editor.InstallDirectory, "Unity.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(foundExe))
                        {
                            unityExePath = foundExe;
                            baseDir = Path.GetDirectoryName(foundExe) ?? editor.InstallDirectory;
                            logs.Add($"[INFO] Located Unity.exe in subfolder: {baseDir}");
                        }
                    }
                    catch
                    {
                        // Ignore permission errors during search
                    }
                }
            }

            // 1. Verify Unity.exe
            bool exeValid = File.Exists(unityExePath);
            if (!exeValid)
            {
                missingItems.Add("Unity.exe executable is missing");
                logs.Add("[FAIL] Unity.exe missing");
            }
            else
            {
                logs.Add($"[OK] Executable verified: {unityExePath}");
            }

            // 2. Verify Data directory & Mono runtime
            var dataDir = Path.Combine(baseDir, "Data");
            var monoDir = Path.Combine(dataDir, "MonoBleedingEdge");
            if (!Directory.Exists(monoDir))
            {
                monoDir = Path.Combine(dataDir, "mono");
            }

            bool monoValid = Directory.Exists(monoDir);
            if (!monoValid)
            {
                missingItems.Add($"Mono runtime directory missing ({Path.Combine(dataDir, "MonoBleedingEdge")})");
                logs.Add("[FAIL] Mono runtime missing in Data/");
            }
            else
            {
                logs.Add($"[OK] Mono runtime verified: {monoDir}");
            }

            // 3. Verify Resources & Managed assemblies
            var managedDir = Path.Combine(dataDir, "Managed");
            if (!Directory.Exists(managedDir))
            {
                missingItems.Add($"Managed assemblies folder missing ({managedDir})");
                logs.Add("[FAIL] Data/Managed missing");
            }
            else
            {
                logs.Add($"[OK] Managed assemblies verified: {managedDir}");
            }

            // 4. Verify Playback Engines
            var playbackEnginesDir = Path.Combine(dataDir, "PlaybackEngines");
            bool playbackEnginesValid = Directory.Exists(playbackEnginesDir);
            if (!playbackEnginesValid)
            {
                logs.Add($"[WARN] Data/PlaybackEngines directory missing at {playbackEnginesDir}");
            }
            else
            {
                logs.Add($"[OK] PlaybackEngines directory verified: {playbackEnginesDir}");

                // Scan installed target platform modules
                foreach (var platform in editor.InstalledTargetPlatforms)
                {
                    if (platform.Id.Equals("StandaloneWindows64", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string moduleFolderName = platform.Id switch
                    {
                        "Android" => "AndroidPlayer",
                        "iOS" => "iOSSupport",
                        "WebGL" => "WebGLSupport",
                        "macOS" => "MacStandaloneSupport",
                        "Linux" => "LinuxStandaloneSupport",
                        _ => platform.Id
                    };

                    var modulePath = Path.Combine(playbackEnginesDir, moduleFolderName);
                    if (!Directory.Exists(modulePath))
                    {
                        missingItems.Add($"Target platform module folder missing: {platform.DisplayName} ({moduleFolderName})");
                        logs.Add($"[FAIL] Module folder missing for {platform.DisplayName}");
                    }
                    else
                    {
                        logs.Add($"[OK] Module {platform.DisplayName} verified");
                    }
                }
            }

            // 5. Verify modules.json integrity (if present)
            var modulesJsonPath = Path.Combine(editor.InstallDirectory, "modules.json");
            if (!File.Exists(modulesJsonPath) && baseDir != editor.InstallDirectory)
            {
                modulesJsonPath = Path.Combine(baseDir, "modules.json");
            }

            if (File.Exists(modulesJsonPath))
            {
                try
                {
                    var text = File.ReadAllText(modulesJsonPath);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        missingItems.Add("modules.json is corrupted (empty file)");
                        logs.Add("[WARN] modules.json is 0 bytes");
                    }
                    else
                    {
                        logs.Add("[OK] modules.json manifest verified");
                    }
                }
                catch (Exception ex)
                {
                    missingItems.Add($"Unable to read modules.json: {ex.Message}");
                    logs.Add($"[WARN] Error reading modules.json: {ex.Message}");
                }
            }

            return new EditorIntegrityReport(
                editor.Version,
                editor.InstallDirectory,
                exeValid ? unityExePath : string.Empty,
                exeValid,
                monoValid,
                playbackEnginesValid,
                missingItems,
                logs
            );
        });
    }

    public Task<bool> RepairInstallationAsync(UnityEditorInfo editor, EditorIntegrityReport report)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(editor.InstallDirectory))
                {
                    return false;
                }

                // Determine base directory
                string baseDir = editor.InstallDirectory;
                var nestedExe = Path.Combine(baseDir, "Editor", "Unity.exe");
                if (File.Exists(nestedExe))
                {
                    baseDir = Path.Combine(baseDir, "Editor");
                }
                else
                {
                    try
                    {
                        var foundExe = Directory.GetFiles(editor.InstallDirectory, "Unity.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (!string.IsNullOrEmpty(foundExe))
                        {
                            baseDir = Path.GetDirectoryName(foundExe) ?? editor.InstallDirectory;
                        }
                    }
                    catch { }
                }

                // Repair 1: Re-create missing empty structure if needed
                var dataDir = Path.Combine(baseDir, "Data");
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }

                var playbackEnginesDir = Path.Combine(dataDir, "PlaybackEngines");
                if (!Directory.Exists(playbackEnginesDir))
                {
                    Directory.CreateDirectory(playbackEnginesDir);
                }

                // Repair 2: Clear lock files or temporary cache files
                var tempFiles = Directory.GetFiles(editor.InstallDirectory, "*.tmp", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(editor.InstallDirectory, "*.lock", SearchOption.AllDirectories));

                foreach (var file in tempFiles)
                {
                    try { File.Delete(file); } catch { }
                }

                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}
