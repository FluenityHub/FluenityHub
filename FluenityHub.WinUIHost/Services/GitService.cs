using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FluenityHub_WinUIHost.Services;

public sealed record GitRepositoryInfo(string? Branch, string? RemoteUrl);

public sealed class GitService
{
    public static bool IsGitInstalled()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            process?.WaitForExit(3000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetCurrentBranch(string projectPath)
    {
        try
        {
            var gitDirectory = ResolveGitDirectory(projectPath);
            if (gitDirectory is null)
            {
                return null;
            }

            var headPath = Path.Combine(gitDirectory, "HEAD");
            if (File.Exists(headPath))
            {
                var text = File.ReadAllText(headPath).Trim();
                if (text.StartsWith("ref: refs/heads/", StringComparison.OrdinalIgnoreCase))
                {
                    return text["ref: refs/heads/".Length..].Trim();
                }
                if (text.Length >= 7)
                {
                    return text[..7];
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    public static string? GetHeadCommit(string projectPath)
    {
        try
        {
            var gitDirectory = ResolveGitDirectory(projectPath);
            if (gitDirectory is null)
            {
                return null;
            }

            var head = File.ReadAllText(Path.Combine(gitDirectory, "HEAD")).Trim();
            const string refPrefix = "ref: ";
            if (!head.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return IsCommitHash(head) ? head : null;
            }

            var reference = head[refPrefix.Length..].Trim();
            var looseReferencePath = Path.Combine(
                gitDirectory,
                reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(looseReferencePath))
            {
                var looseHash = File.ReadAllText(looseReferencePath).Trim();
                return IsCommitHash(looseHash) ? looseHash : null;
            }

            var packedReferencesPath = Path.Combine(gitDirectory, "packed-refs");
            if (!File.Exists(packedReferencesPath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(packedReferencesPath))
            {
                if (line.Length < 42 || line[0] is '#' or '^')
                {
                    continue;
                }

                var separator = line.IndexOf(' ');
                if (separator <= 0
                    || !string.Equals(line[(separator + 1)..], reference, StringComparison.Ordinal))
                {
                    continue;
                }

                var packedHash = line[..separator];
                return IsCommitHash(packedHash) ? packedHash : null;
            }
        }
        catch
        {
            // A repository can be updated while the project menu is opening.
        }

        return null;
    }

    private static bool IsCommitHash(string value)
        => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    public static GitRepositoryInfo? GetRepositoryInfo(string projectPath)
    {
        var gitDirectory = ResolveGitDirectory(projectPath);
        if (gitDirectory is null)
        {
            return null;
        }

        return new GitRepositoryInfo(
            GetCurrentBranch(projectPath),
            ReadOriginUrl(Path.Combine(gitDirectory, "config")));
    }

    public static string? GetConfigurationPath(string projectPath)
    {
        var gitDirectory = ResolveGitDirectory(projectPath);
        return gitDirectory is null ? null : Path.Combine(gitDirectory, "config");
    }

    public static Task<(bool Success, string ErrorMessage)> RemoveOriginAsync(string projectPath)
    {
        return Task.Run(() =>
        {
            var repository = GetRepositoryInfo(projectPath);
            if (repository is null)
            {
                return (false, "The Git repository could not be found.");
            }

            if (string.IsNullOrWhiteSpace(repository.RemoteUrl))
            {
                return (true, string.Empty);
            }

            var (success, output) = RunGit(projectPath, ["remote", "remove", "origin"]);
            return success
                ? (true, string.Empty)
                : (false, string.IsNullOrWhiteSpace(output)
                    ? "Git could not remove the origin remote."
                    : output);
        });
    }

    private static string? ResolveGitDirectory(string projectPath)
    {
        try
        {
            var markerPath = Path.Combine(projectPath, ".git");
            if (Directory.Exists(markerPath))
            {
                return markerPath;
            }

            if (!File.Exists(markerPath))
            {
                return null;
            }

            var marker = File.ReadAllText(markerPath).Trim();
            const string prefix = "gitdir:";
            if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var gitDirectory = marker[prefix.Length..].Trim();
            if (!Path.IsPathRooted(gitDirectory))
            {
                gitDirectory = Path.GetFullPath(Path.Combine(projectPath, gitDirectory));
            }

            return Directory.Exists(gitDirectory) ? gitDirectory : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadOriginUrl(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return null;
            }

            var inOriginSection = false;
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    inOriginSection = string.Equals(
                        line,
                        "[remote \"origin\"]",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inOriginSection)
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0
                    || !string.Equals(line[..separator].Trim(), "url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var remoteUrl = line[(separator + 1)..].Trim();
                return string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl;
            }
        }
        catch
        {
            // Treat malformed or inaccessible configuration as a local repository.
        }

        return null;
    }

    public async Task<(bool Success, string Message)> InitAndSetupUnityGitAsync(
        string projectPath,
        string remoteUrl,
        string branchName,
        bool enableLfs,
        bool pushAllChanges,
        string? credentialUser = null,
        string? credentialPassword = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(projectPath))
                {
                    return (false, "Project directory does not exist.");
                }

                var hasRemote = !string.IsNullOrWhiteSpace(remoteUrl);
                if (hasRemote && !IsValidRemoteUrl(remoteUrl))
                {
                    return (false, "Remote URL must be a valid HTTPS or SSH Git URL.");
                }

                if (hasRemote
                    && pushAllChanges
                    && !NetworkConnectivityService.Current.CanAttemptInternet)
                {
                    return (false, NetworkConnectivityService.OfflineMessage);
                }

                // 1. Create Unity .gitignore if missing or empty
                EnsureUnityGitIgnore(projectPath);

                // 2. Initialize Git if not already initialized
                var gitDir = Path.Combine(projectPath, ".git");
                if (!Directory.Exists(gitDir))
                {
                    var (initSuccess, initErr) = RunGit(projectPath, ["init"]);
                    if (!initSuccess) return (false, $"git init failed: {initErr}");
                }

                // 3. Configure Git LFS if enabled
                if (enableLfs)
                {
                    EnsureUnityGitAttributes(projectPath);
                    RunGit(projectPath, ["lfs", "install"]);
                }

                // 4. Set default branch name
                branchName = string.IsNullOrWhiteSpace(branchName) ? "main" : branchName.Trim();
                if (!IsSafeBranchName(branchName))
                {
                    return (false, "Branch name contains unsafe characters.");
                }

                RunGit(projectPath, ["branch", "-M", branchName]);

                // 5. Add or update remote origin when one was supplied.
                if (hasRemote)
                {
                    var (setRemoteSuccess, _) = RunGit(projectPath, ["remote", "set-url", "origin", remoteUrl]);
                    if (!setRemoteSuccess)
                    {
                        var (addRemoteSuccess, addRemoteErr) = RunGit(projectPath, ["remote", "add", "origin", remoteUrl]);
                        if (!addRemoteSuccess)
                        {
                            return (false, $"Failed to configure git remote: {addRemoteErr}");
                        }
                    }
                }

                // 6. Create an initial commit when requested, then push when a remote exists.
                if (pushAllChanges)
                {
                    RunGit(projectPath, ["add", "."]);
                    RunGit(projectPath, ["commit", "-m", "Initial commit from FluenityHub"]);

                    if (hasRemote)
                    {
                        var (pushSuccess, pushErr) = !string.IsNullOrWhiteSpace(credentialUser)
                            && !string.IsNullOrWhiteSpace(credentialPassword)
                            ? RunGitWithHttpCredential(
                                projectPath,
                                ["push", "-u", "origin", branchName],
                                remoteUrl,
                                credentialUser.Trim(),
                                credentialPassword.Trim())
                            : RunGit(projectPath, ["push", "-u", "origin", branchName]);
                        if (!pushSuccess)
                        {
                            return (true, $"Repository created and connected, but push failed: {pushErr}");
                        }
                    }
                }

                return hasRemote
                    ? (true, "Successfully connected project to remote repository!")
                    : (true, "Successfully initialized the local Git repository!");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    private static (bool Success, string Output) RunGit(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var (name, value) in environmentVariables)
            {
                process.StartInfo.Environment[name] = value;
            }
        }

        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, "Git command timed out.");
        }

        bool success = process.ExitCode == 0;
        string resultStr = success ? output.ToString().Trim() : error.ToString().Trim();
        return (success, resultStr);
    }

    private static (bool Success, string Output) RunGitWithHttpCredential(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string remoteUrl,
        string username,
        string password)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return RunGit(workingDirectory, arguments);
        }

        var credentialBytes = Encoding.UTF8.GetBytes($"{username}:{password}");
        try
        {
            var authorization = Convert.ToBase64String(credentialBytes);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "credential.helper",
                ["GIT_CONFIG_VALUE_0"] = string.Empty,
                ["GIT_CONFIG_KEY_1"] = "http.extraHeader",
                ["GIT_CONFIG_VALUE_1"] = $"Authorization: Basic {authorization}",
                ["GIT_TERMINAL_PROMPT"] = "0"
            };

            return RunGit(workingDirectory, arguments, environmentVariables: environment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credentialBytes);
        }
    }

    public static bool IsValidRemoteUrl(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.Length > 2048)
        {
            return false;
        }

        if (remoteUrl.Any(char.IsControl) || remoteUrl.Contains(' '))
        {
            return false;
        }

        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeSsh, StringComparison.OrdinalIgnoreCase);
        }

        var colonIndex = remoteUrl.IndexOf(':');
        var slashIndex = remoteUrl.IndexOf('/');
        return colonIndex > 0
            && slashIndex > colonIndex
            && remoteUrl[..colonIndex].Contains('@')
            && remoteUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeBranchName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName)
            || branchName.Length > 128
            || branchName.StartsWith("-", StringComparison.Ordinal)
            || branchName.EndsWith(".", StringComparison.Ordinal)
            || branchName.EndsWith("/", StringComparison.Ordinal)
            || branchName.Contains("..", StringComparison.Ordinal)
            || branchName.Contains("//", StringComparison.Ordinal)
            || branchName.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in branchName)
        {
            if (char.IsLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static void EnsureUnityGitIgnore(string projectPath)
    {
        var ignorePath = Path.Combine(projectPath, ".gitignore");
        if (File.Exists(ignorePath) && new FileInfo(ignorePath).Length > 0)
        {
            return;
        }

        const string unityGitIgnore = """
            # Unity auto-generated files
            /[Ll]ibrary/
            /[Tt]emp/
            /[Oo]bj/
            /[Bb]uild/
            /[Bb]uilds/
            /[Ll]ogs/
            /[Uu]ser[Ss]ettings/
            /[Mm]emoryCaptures/
            /[Aa]sset[Ss]tore[Tt]ools*

            # VS Code / Visual Studio
            .vscode/
            .idea/
            *.csproj
            *.unityproj
            *.sln
            *.suo
            *.user
            *.userprefs
            *.pidb
            *.booproj
            *.svd
            *.pdb
            *.opendb
            *.VC.db

            # OS generated
            .DS_Store
            Thumbs.db
            """;

        File.WriteAllText(ignorePath, unityGitIgnore.Trim(), Encoding.UTF8);
    }

    public static void EnsureUnityGitAttributes(string projectPath)
    {
        var attrPath = Path.Combine(projectPath, ".gitattributes");
        if (File.Exists(attrPath) && new FileInfo(attrPath).Length > 0)
        {
            return;
        }

        const string unityGitAttributes = """
            # Auto-detected LFS patterns for Unity
            *.3gp filter=lfs diff=lfs merge=lfs -text
            *.7z filter=lfs diff=lfs merge=lfs -text
            *.a filter=lfs diff=lfs merge=lfs -text
            *.aar filter=lfs diff=lfs merge=lfs -text
            *.anim filter=lfs diff=lfs merge=lfs -text
            *.apk filter=lfs diff=lfs merge=lfs -text
            *.asset filter=lfs diff=lfs merge=lfs -text
            *.bundle filter=lfs diff=lfs merge=lfs -text
            *.cab filter=lfs diff=lfs merge=lfs -text
            *.dll filter=lfs diff=lfs merge=lfs -text
            *.dylib filter=lfs diff=lfs merge=lfs -text
            *.exr filter=lfs diff=lfs merge=lfs -text
            *.fbx filter=lfs diff=lfs merge=lfs -text
            *.jpeg filter=lfs diff=lfs merge=lfs -text
            *.jpg filter=lfs diff=lfs merge=lfs -text
            *.mat filter=lfs diff=lfs merge=lfs -text
            *.mp3 filter=lfs diff=lfs merge=lfs -text
            *.mp4 filter=lfs diff=lfs merge=lfs -text
            *.obj filter=lfs diff=lfs merge=lfs -text
            *.ogg filter=lfs diff=lfs merge=lfs -text
            *.otf filter=lfs diff=lfs merge=lfs -text
            *.pdf filter=lfs diff=lfs merge=lfs -text
            *.png filter=lfs diff=lfs merge=lfs -text
            *.prefab filter=lfs diff=lfs merge=lfs -text
            *.psd filter=lfs diff=lfs merge=lfs -text
            *.so filter=lfs diff=lfs merge=lfs -text
            *.tga filter=lfs diff=lfs merge=lfs -text
            *.ttf filter=lfs diff=lfs merge=lfs -text
            *.unitypackage filter=lfs diff=lfs merge=lfs -text
            *.wav filter=lfs diff=lfs merge=lfs -text
            *.zip filter=lfs diff=lfs merge=lfs -text
            """;

        File.WriteAllText(attrPath, unityGitAttributes.Trim(), Encoding.UTF8);
    }

    public static async Task<(bool Success, string Message)> CloneRepositoryAsync(string cloneUrl, string targetDirectory, string? branch = null)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (false, NetworkConnectivityService.OfflineMessage);
        }

        if (string.IsNullOrWhiteSpace(cloneUrl) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return (false, "Repository URL and target location are required.");
        }
        if (!IsValidRemoteUrl(cloneUrl))
        {
            return (false, "Repository URL must be a valid HTTPS or SSH Git URL.");
        }

        try
        {
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("clone");
            if (!string.IsNullOrWhiteSpace(branch))
            {
                startInfo.ArgumentList.Add("--branch");
                startInfo.ArgumentList.Add(branch);
            }
            startInfo.ArgumentList.Add(cloneUrl);
            startInfo.ArgumentList.Add(targetDirectory);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "Failed to start Git process.");
            }

            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string err = await errorTask;
            if (process.ExitCode != 0)
            {
                return (false, string.IsNullOrWhiteSpace(err) ? "Git clone exited with non-zero code." : err.Trim());
            }

            return (true, "Repository cloned successfully.");
        }
        catch (Exception ex)
        {
            return (false, $"Clone failed: {ex.Message}");
        }
    }
}
