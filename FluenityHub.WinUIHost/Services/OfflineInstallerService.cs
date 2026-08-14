using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public record OfflineInstallerInfo(
    string FilePath,
    string FileName,
    string DetectedVersion,
    string ModuleType, // "Editor", "Android", "iOS", "WebGL", "Documentation", "Unknown"
    bool IsValidInstaller
);

public class OfflineInstallerService
{
    public Task<OfflineInstallerInfo> InspectInstallerAsync(string filePath)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(filePath))
            {
                return new OfflineInstallerInfo(filePath, Path.GetFileName(filePath), "Unknown", "Unknown", IsValidInstaller: false);
            }

            string fileName = Path.GetFileName(filePath);

            // 1. Check if it's a Download Assistant JSON manifest
            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string content = File.ReadAllText(filePath);
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("version", out var verProp))
                    {
                        return new OfflineInstallerInfo(filePath, fileName, verProp.GetString() ?? "Unknown", "DownloadAssistantManifest", IsValidInstaller: true);
                    }
                }
                catch { }
            }

            // 2. Parse Unity setup executable names (e.g., UnitySetup64-2022.3.15f1.exe, UnitySetup-Android-Support-for-Editor-6000.0.32f1.exe)
            string detectedVersion = "Unknown";
            var match = Regex.Match(fileName, @"(\d{4}\.\d+\.\d+[a-z0-9]+)");
            if (match.Success)
            {
                detectedVersion = match.Groups[1].Value;
            }

            string moduleType = "Unknown";
            if (fileName.Contains("UnitySetup64", StringComparison.OrdinalIgnoreCase) || fileName.Equals("UnitySetup.exe", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "Editor";
            }
            else if (fileName.Contains("Android", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "Android";
            }
            else if (fileName.Contains("iOS", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "iOS";
            }
            else if (fileName.Contains("WebGL", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "WebGL";
            }
            else if (fileName.Contains("Mac", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "macOS";
            }
            else if (fileName.Contains("Linux", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "Linux";
            }
            else if (fileName.Contains("Documentation", StringComparison.OrdinalIgnoreCase))
            {
                moduleType = "Documentation";
            }

            bool isValid = moduleType != "Unknown" || detectedVersion != "Unknown";

            return new OfflineInstallerInfo(filePath, fileName, detectedVersion, moduleType, isValid);
        });
    }

    public Task<bool> InstallOrRepairFromOfflinePackageAsync(string installerPath, string targetInstallDirectory, Action<string>? logCallback = null)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!File.Exists(installerPath))
                {
                    logCallback?.Invoke($"[FAIL] Installer file not found: {installerPath}");
                    return false;
                }

                if (!Directory.Exists(targetInstallDirectory))
                {
                    Directory.CreateDirectory(targetInstallDirectory);
                    logCallback?.Invoke($"[OK] Created target installation directory: {targetInstallDirectory}");
                }

                logCallback?.Invoke($"[INFO] Executing offline silent setup: {Path.GetFileName(installerPath)}");

                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = $"/S /D={targetInstallDirectory}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    Verb = "runas"
                };

                using var process = Process.Start(psi);
                if (process is null)
                {
                    logCallback?.Invoke("[FAIL] Failed to launch installer process");
                    return false;
                }

                process.WaitForExit();
                bool success = process.ExitCode == 0;

                if (success)
                {
                    logCallback?.Invoke($"[OK] Offline setup completed cleanly for {Path.GetFileName(installerPath)}");
                }
                else
                {
                    logCallback?.Invoke($"[WARN] Installer finished with exit code {process.ExitCode}");
                }

                return success;
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"[FAIL] Offline installation error: {ex.Message}");
                return false;
            }
        });
    }
}
