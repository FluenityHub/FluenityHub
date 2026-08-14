using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FluenityHub_WinUIHost.Services;

public sealed record IdeInfo(string Name, string ExecutablePath, string Glyph);

public sealed class IdeDetector
{
    public List<IdeInfo> GetInstalledIdes()
    {
        var ides = new List<IdeInfo>();

        // 1. VS Code
        var vsCodePath = FindVsCode();
        if (!string.IsNullOrEmpty(vsCodePath) && File.Exists(vsCodePath))
        {
            ides.Add(new IdeInfo("Visual Studio Code", vsCodePath, "\uE737"));
        }

        // 2. Visual Studio (Community / Professional / Enterprise)
        var vsPath = FindVisualStudio();
        if (!string.IsNullOrEmpty(vsPath) && File.Exists(vsPath))
        {
            ides.Add(new IdeInfo("Visual Studio", vsPath, "\uE737"));
        }

        // 3. JetBrains Rider
        var riderPath = FindRider();
        if (!string.IsNullOrEmpty(riderPath) && File.Exists(riderPath))
        {
            ides.Add(new IdeInfo("JetBrains Rider", riderPath, "\uE737"));
        }

        return ides;
    }

    public static bool LaunchIde(string idePath, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(idePath) || !File.Exists(idePath) || string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = idePath,
                Arguments = $"\"{projectPath}\"",
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindVsCode()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidatePaths = [
            Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe")
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string? FindVisualStudio()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (File.Exists(vswhere))
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-latest -property installationPath",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process is not null)
                {
                    var installPath = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(2000);
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        var devenv = Path.Combine(installPath, "Common7", "IDE", "devenv.exe");
                        if (File.Exists(devenv)) return devenv;
                    }
                }
            }
            catch
            {
                // Fallback to manual check
            }
        }

        // Direct candidate check
        string[] editions = ["Community", "Professional", "Enterprise", "BuildTools"];
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var edition in editions)
        {
            var devenv = Path.Combine(pf, "Microsoft Visual Studio", "2022", edition, "Common7", "IDE", "devenv.exe");
            if (File.Exists(devenv)) return devenv;
        }

        return null;
    }

    private static string? FindRider()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Standard install
        var riderLocal = Path.Combine(localAppData, "Programs", "Rider", "bin", "rider64.exe");
        if (File.Exists(riderLocal)) return riderLocal;

        // JetBrains Toolbox installs
        var toolboxDir = Path.Combine(localAppData, "JetBrains", "Toolbox", "apps", "Rider");
        if (Directory.Exists(toolboxDir))
        {
            try
            {
                var riderExe = Directory.EnumerateFiles(toolboxDir, "rider64.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (riderExe is not null) return riderExe;
            }
            catch { }
        }

        // Program Files installs
        var jetbrainsDir = Path.Combine(programFiles, "JetBrains");
        if (Directory.Exists(jetbrainsDir))
        {
            try
            {
                var riderExe = Directory.EnumerateFiles(jetbrainsDir, "rider64.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (riderExe is not null) return riderExe;
            }
            catch { }
        }

        return null;
    }
}
