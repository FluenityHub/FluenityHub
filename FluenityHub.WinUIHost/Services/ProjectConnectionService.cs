using System.Text;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record ProjectDisconnectResult(bool Success, string Message);

public sealed partial class ProjectConnectionService
{
    private readonly UnityHubProjectService _projectService;

    public ProjectConnectionService(UnityHubProjectService projectService)
    {
        _projectService = projectService;
    }

    public Task<ProjectDisconnectResult> DisconnectSourceControlAsync(UnityProjectInfo project)
    {
        return Task.Run(async () =>
        {
            if (!Directory.Exists(project.Path))
            {
                return new ProjectDisconnectResult(false, "The project folder could not be found.");
            }

            if (IsUnityVersionControl(project))
            {
                return DisconnectUnityVersionControl(project);
            }

            return await DisconnectGitAsync(project);
        });
    }

    public Task<ProjectDisconnectResult> DisconnectCloudAsync(UnityProjectInfo project)
    {
        return Task.Run(() => DisconnectCloud(project));
    }

    public static string? GetUnityVersionControlMetadataPath(UnityProjectInfo project)
        => ResolvePlasticDirectory(project);

    public static bool IsUnityVersionControl(UnityProjectInfo project)
        => IsUnityVersionControlProvider(project.SourceControlProvider)
           || IsUnityVersionControlProvider(project.ConfiguredSourceControlProvider);

    private async Task<ProjectDisconnectResult> DisconnectGitAsync(UnityProjectInfo project)
    {
        var configPath = GitService.GetConfigurationPath(project.Path);
        byte[]? originalConfig = null;
        if (project.SourceControlHasRemote)
        {
            if (configPath is null || !File.Exists(configPath))
            {
                return new ProjectDisconnectResult(false, "The Git configuration could not be found.");
            }

            originalConfig = await File.ReadAllBytesAsync(configPath);
            var removeResult = await GitService.RemoveOriginAsync(project.Path);
            if (!removeResult.Success)
            {
                return new ProjectDisconnectResult(false, removeResult.ErrorMessage);
            }
        }

        try
        {
            if (_projectService.DisconnectProjectFromSourceControl(project.Path))
            {
                return new ProjectDisconnectResult(true, string.Empty);
            }

            if (originalConfig is not null && configPath is not null)
            {
                await WriteBytesAtomicallyAsync(configPath, originalConfig);
            }

            return new ProjectDisconnectResult(
                false,
                "Unity Hub's project data could not be updated. The Git configuration was restored.");
        }
        catch
        {
            if (originalConfig is not null && configPath is not null)
            {
                await WriteBytesAtomicallyAsync(configPath, originalConfig);
            }

            throw;
        }
    }

    private ProjectDisconnectResult DisconnectUnityVersionControl(UnityProjectInfo project)
    {
        var plasticDirectory = ResolvePlasticDirectory(project);
        if (plasticDirectory is null)
        {
            return new ProjectDisconnectResult(
                false,
                "The Unity Version Control workspace metadata could not be found.");
        }

        var stagedDirectory = StageDirectoryForRemoval(plasticDirectory);
        try
        {
            if (!_projectService.DisconnectProjectFromSourceControl(project.Path))
            {
                RestoreStagedDirectory(stagedDirectory, plasticDirectory);
                return new ProjectDisconnectResult(
                    false,
                    "Unity Hub's project data could not be updated. The workspace connection was restored.");
            }

            return DeleteStagedDirectory(stagedDirectory);
        }
        catch
        {
            RestoreStagedDirectory(stagedDirectory, plasticDirectory);
            throw;
        }
    }

    private ProjectDisconnectResult DisconnectCloud(UnityProjectInfo project)
    {
        if (!Directory.Exists(project.Path))
        {
            return new ProjectDisconnectResult(false, "The project folder could not be found.");
        }

        var settingsPath = Path.Combine(project.Path, "ProjectSettings", "ProjectSettings.asset");
        if (!File.Exists(settingsPath))
        {
            return new ProjectDisconnectResult(false, "ProjectSettings.asset could not be found.");
        }

        var disconnectVersionControl = IsUnityVersionControl(project);
        var originalSettings = File.ReadAllBytes(settingsPath);
        string? plasticDirectory = null;
        string? stagedDirectory = null;
        if (disconnectVersionControl)
        {
            plasticDirectory = ResolvePlasticDirectory(project);
            if (plasticDirectory is null)
            {
                return new ProjectDisconnectResult(
                    false,
                    "The Unity Version Control workspace metadata could not be found.");
            }

        }

        try
        {
            if (plasticDirectory is not null)
            {
                stagedDirectory = StageDirectoryForRemoval(plasticDirectory);
            }

            var currentText = File.ReadAllText(settingsPath);
            var updatedText = CloudSettingPattern().Replace(
                currentText,
                static match => $"{match.Groups["indent"].Value}{match.Groups["key"].Value}: ");
            WriteTextAtomically(settingsPath, updatedText);

            if (!_projectService.DisconnectProjectFromCloud(project.Path, disconnectVersionControl))
            {
                WriteBytesAtomically(settingsPath, originalSettings);
                if (stagedDirectory is not null && plasticDirectory is not null)
                {
                    RestoreStagedDirectory(stagedDirectory, plasticDirectory);
                }

                return new ProjectDisconnectResult(
                    false,
                    "Unity Hub's project data could not be updated. The project connection was restored.");
            }

            return stagedDirectory is null
                ? new ProjectDisconnectResult(true, string.Empty)
                : DeleteStagedDirectory(stagedDirectory);
        }
        catch
        {
            WriteBytesAtomically(settingsPath, originalSettings);
            if (stagedDirectory is not null && plasticDirectory is not null)
            {
                RestoreStagedDirectory(stagedDirectory, plasticDirectory);
            }

            throw;
        }
    }

    private static string StageDirectoryForRemoval(string directoryPath)
    {
        var parent = Path.GetDirectoryName(directoryPath)
            ?? throw new IOException("The workspace metadata directory has no parent folder.");
        var stagedPath = Path.Combine(
            parent,
            $".plastic.fluenity-disconnect-{Guid.NewGuid():N}");
        Directory.Move(directoryPath, stagedPath);
        return stagedPath;
    }

    private static ProjectDisconnectResult DeleteStagedDirectory(string stagedDirectory)
    {
        try
        {
            Directory.Delete(stagedDirectory, recursive: true);
            return new ProjectDisconnectResult(true, string.Empty);
        }
        catch (IOException)
        {
            return new ProjectDisconnectResult(
                true,
                $"Disconnected, but temporary workspace metadata remains at '{stagedDirectory}'.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ProjectDisconnectResult(
                true,
                $"Disconnected, but temporary workspace metadata remains at '{stagedDirectory}'.");
        }
    }

    private static void RestoreStagedDirectory(string stagedDirectory, string originalDirectory)
    {
        if (Directory.Exists(stagedDirectory) && !Directory.Exists(originalDirectory))
        {
            Directory.Move(stagedDirectory, originalDirectory);
        }
    }

    private static string? ResolvePlasticDirectory(UnityProjectInfo project)
    {
        if (!string.IsNullOrWhiteSpace(project.ConfiguredSourceControlPath))
        {
            var configuredPath = Path.GetFullPath(project.ConfiguredSourceControlPath);
            var configuredDirectory = string.Equals(
                Path.GetFileName(configuredPath),
                ".plastic",
                StringComparison.OrdinalIgnoreCase)
                    ? configuredPath
                    : Path.Combine(configuredPath, ".plastic");
            if (Directory.Exists(configuredDirectory)
                && IsPathWithin(project.Path, Path.GetDirectoryName(configuredDirectory)!))
            {
                return configuredDirectory;
            }
        }

        var current = new DirectoryInfo(Path.GetFullPath(project.Path));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".plastic");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static bool IsPathWithin(string path, string candidateParent)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateParent));
        return string.Equals(fullPath, fullParent, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(
                   fullParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnityVersionControlProvider(string? provider)
        => string.Equals(provider, SourceControlDetectionService.UnityVersionControlProvider, StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "unity-version-control", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "uvcs", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "plastic", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"^(?<indent>[ \t]*)(?<key>organizationId|cloudProjectId|projectName|genesisOrgId):.*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex CloudSettingPattern();

    private static async Task WriteBytesAtomicallyAsync(string path, byte[] content)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteBytesAtomically(string path, byte[] content)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
