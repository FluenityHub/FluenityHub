using System;
using System.IO;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record SourceControlDetectionResult(
    string Provider,
    string? Branch,
    string? Revision,
    string? RemoteUrl,
    string? Repository,
    bool HasRemote);

public static class SourceControlDetectionService
{
    public const string GitProvider = "Git";
    public const string UnityVersionControlProvider = "Unity Version Control";

    public const string GitHubProvider = "GitHub";
    public const string GitLabProvider = "GitLab";

    public static SourceControlDetectionResult? Detect(UnityProjectInfo project)
    {
        var projectPath = project.Path;
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            return null;
        }

        if (project.IsSourceControlDisconnected)
        {
            return null;
        }

        var configuredProvider = project.ConfiguredSourceControlProvider?.Trim();
        var unityVersionControl = TryGetUnityVersionControlWorkspace(projectPath);
        var hasUnityVersionControlMarker = unityVersionControl is not null
            || Directory.Exists(Path.Combine(projectPath, ".plastic"))
            || File.Exists(Path.Combine(projectPath, "plastic.selector"))
            || HasMarkerInProjectOrParent(projectPath, "plastic.workspace");

        // Unity Hub's project metadata is authoritative. A Unity Version Control
        // workspace can also contain a .git directory, so probing Git first
        // incorrectly reclassifies connected UVCS projects as Git repositories.
        if (IsUnityVersionControlProvider(configuredProvider))
        {
            return CreateUnityVersionControlResult(project, unityVersionControl);
        }

        var gitRepository = GitService.GetRepositoryInfo(projectPath);
        if (gitRepository is not null && IsGitProvider(configuredProvider))
        {
            return CreateGitResult(project, gitRepository, configuredProvider);
        }

        // A parsed Plastic selector is stronger evidence than a generic .git
        // marker when Unity Hub did not persist a provider for the project.
        if (unityVersionControl is not null || (hasUnityVersionControlMarker && gitRepository is null))
        {
            return CreateUnityVersionControlResult(project, unityVersionControl);
        }

        if (gitRepository is not null)
        {
            return CreateGitResult(project, gitRepository, configuredProvider);
        }

        if (hasUnityVersionControlMarker)
        {
            return CreateUnityVersionControlResult(project, unityVersionControl);
        }

        return null;
    }

    private static SourceControlDetectionResult CreateGitResult(
        UnityProjectInfo project,
        GitRepositoryInfo gitRepository,
        string? configuredProvider)
    {
        var provider = InferGitProvider(gitRepository.RemoteUrl, configuredProvider);
        var repository = ParseRepositoryName(gitRepository.RemoteUrl)
            ?? CombineRepositoryName(
                project.ConfiguredSourceControlOrganization,
                project.ConfiguredSourceControlRepository);

        return new SourceControlDetectionResult(
            provider,
            gitRepository.Branch,
            GitService.GetHeadCommit(project.Path),
            gitRepository.RemoteUrl,
            repository,
            !string.IsNullOrWhiteSpace(gitRepository.RemoteUrl));
    }

    private static SourceControlDetectionResult CreateUnityVersionControlResult(
        UnityProjectInfo project,
        UnityVersionControlWorkspaceInfo? workspace)
    {
        var organization = project.ConfiguredSourceControlOrganization ?? workspace?.Organization;
        var repository = project.ConfiguredSourceControlRepository ?? workspace?.Repository;

        return new SourceControlDetectionResult(
            UnityVersionControlProvider,
            workspace?.Branch,
            workspace?.Changeset,
            null,
            CombineRepositoryName(organization, repository),
            !string.IsNullOrWhiteSpace(organization) && !string.IsNullOrWhiteSpace(repository));
    }

    private static bool IsUnityVersionControlProvider(string? provider)
        => string.Equals(provider, "unity-version-control", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "uvcs", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "plastic", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, UnityVersionControlProvider, StringComparison.OrdinalIgnoreCase);

    private static bool IsGitProvider(string? provider)
        => string.Equals(provider, "git", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "github", StringComparison.OrdinalIgnoreCase)
           || string.Equals(provider, "gitlab", StringComparison.OrdinalIgnoreCase);

    private static UnityVersionControlWorkspaceInfo? TryGetUnityVersionControlWorkspace(string projectPath)
    {
        var selectorPaths = new[]
        {
            Path.Combine(projectPath, ".plastic", "plastic.selector"),
            Path.Combine(projectPath, "plastic.selector")
        };

        foreach (var selectorPath in selectorPaths)
        {
            if (!File.Exists(selectorPath))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(
                    selectorPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var selector = reader.ReadToEnd();

                var branchMatch = Regex.Match(
                    selector,
                    "(?:br|smartbranch|branch)\\s+\"(/[^\"]+)\"",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var changesetMatch = Regex.Match(
                    selector,
                    "co\\s+\"cs:(\\d+)\"",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var repositoryMatch = Regex.Match(
                    selector,
                    "repository\\s+\"([^@\"\\r\\n]+)@([^@\"\\r\\n]+)@([^\"\\r\\n]+)\"",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                return new UnityVersionControlWorkspaceInfo(
                    branchMatch.Success ? branchMatch.Groups[1].Value.Trim() : null,
                    changesetMatch.Success ? changesetMatch.Groups[1].Value.Trim() : null,
                    repositoryMatch.Success ? repositoryMatch.Groups[2].Value.Trim() : null,
                    repositoryMatch.Success ? repositoryMatch.Groups[1].Value.Trim() : null);
            }
            catch (IOException)
            {
                // Treat a temporarily locked selector as an unparsed UVCS marker.
            }
            catch (UnauthorizedAccessException)
            {
                // The provider metadata can still identify the workspace.
            }
        }

        return null;
    }

    private static string InferGitProvider(string? remoteUrl, string? configuredProvider)
    {
        var host = TryGetRemoteHost(remoteUrl);
        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return GitHubProvider;
        }

        if (string.Equals(host, "gitlab.com", StringComparison.OrdinalIgnoreCase))
        {
            return GitLabProvider;
        }

        var normalizedConfiguredProvider = configuredProvider?.Trim().ToLowerInvariant();
        return normalizedConfiguredProvider switch
        {
            "github" => GitHubProvider,
            "gitlab" => GitLabProvider,
            _ => GitProvider
        };
    }

    private static string? TryGetRemoteHost(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        var atIndex = remoteUrl.IndexOf('@');
        var colonIndex = remoteUrl.IndexOf(':', atIndex + 1);
        return atIndex >= 0 && colonIndex > atIndex
            ? remoteUrl[(atIndex + 1)..colonIndex]
            : null;
    }

    private static string? ParseRepositoryName(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        string path;
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            var colonIndex = remoteUrl.IndexOf(':');
            path = colonIndex >= 0 ? remoteUrl[(colonIndex + 1)..] : remoteUrl;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? CombineRepositoryName(string? organization, string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(organization)
            ? repository
            : $"{organization}/{repository}";
    }

    private static bool HasMarkerInProjectOrParent(string projectPath, string markerName)
    {
        var directory = new DirectoryInfo(projectPath);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, markerName)))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record UnityVersionControlWorkspaceInfo(
        string? Branch,
        string? Changeset,
        string? Organization,
        string? Repository);
}
