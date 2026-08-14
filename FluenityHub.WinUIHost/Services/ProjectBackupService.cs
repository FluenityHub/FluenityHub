using System.Text.Json;
using System.Text.Json.Nodes;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class ProjectBackupService
{
    private const string ManifestFileName = ".fluenity-backup.json";
    private const int BufferSize = 1024 * 1024;

    private static readonly HashSet<string> AlwaysExcludedRootDirectories = new(
        ["Library", "Temp", "Logs", "obj", ".vs", "Build", "Builds"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim RegistryGate = new(1, 1);

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _registryPath;

    public ProjectBackupService(string? registryPath = null)
    {
        var appDataDirectory = registryPath is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FluenityHub.WinUIHost")
            : Path.GetDirectoryName(Path.GetFullPath(registryPath))
              ?? throw new ArgumentException("The backup registry path is invalid.", nameof(registryPath));
        Directory.CreateDirectory(appDataDirectory);
        _registryPath = registryPath is null
            ? Path.Combine(appDataDirectory, "project-backups.json")
            : Path.GetFullPath(registryPath);
    }

    public static string DefaultBackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "FluenityHub Backups");

    public static IReadOnlyList<string> ExcludedDirectoryNames =>
        AlwaysExcludedRootDirectories.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<ProjectBackupRecord> CreateBackupAsync(
        UnityProjectInfo project,
        string targetPath,
        bool includeUserSettings,
        bool includeGitHistory,
        IProgress<ProjectCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateUnityProject(project.Path);
        ValidateTargetPath(project.Path, targetPath);

        var backupId = Guid.NewGuid().ToString("N");
        var record = await CopyProjectAsync(
            project.Path,
            targetPath,
            includeUserSettings,
            includeGitHistory,
            skipManifest: true,
            progress,
            cancellationToken,
            async (totalBytes, stagingPath) =>
            {
                var created = new ProjectBackupRecord
                {
                    Id = backupId,
                    SourceProjectPath = NormalizePath(project.Path),
                    ProjectTitle = project.Title,
                    UnityVersion = project.Version,
                    BackupPath = NormalizePath(targetPath),
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    TotalBytes = totalBytes,
                    IncludesUserSettings = includeUserSettings,
                    IncludesGitHistory = includeGitHistory
                };

                await WriteManifestAsync(created, stagingPath, cancellationToken);
                return created;
            }).ConfigureAwait(false);

        await AddRecordAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task CloneProjectAsync(
        string sourceProjectPath,
        string targetPath,
        bool includeUserSettings,
        bool includeGitHistory,
        IProgress<ProjectCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateUnityProject(sourceProjectPath);
        ValidateTargetPath(sourceProjectPath, targetPath);

        await CopyProjectAsync<object?>(
            sourceProjectPath,
            targetPath,
            includeUserSettings,
            includeGitHistory,
            skipManifest: true,
            progress,
            cancellationToken,
            (_, _) => Task.FromResult<object?>(null)).ConfigureAwait(false);
    }

    public async Task RestoreBackupAsNewProjectAsync(
        ProjectBackupRecord backup,
        string targetPath,
        bool includeGitHistory,
        IProgress<ProjectCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        await ValidateBackupAsync(backup, cancellationToken).ConfigureAwait(false);
        ValidateTargetPath(backup.BackupPath, targetPath);

        await CopyProjectAsync<object?>(
            backup.BackupPath,
            targetPath,
            includeUserSettings: true,
            includeGitHistory,
            skipManifest: true,
            progress,
            cancellationToken,
            (_, _) => Task.FromResult<object?>(null)).ConfigureAwait(false);

        ValidateUnityProject(targetPath);
    }

    public async Task<IReadOnlyList<ProjectBackupRecord>> GetBackupsForProjectAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectPath = NormalizePath(projectPath);
        var records = await LoadRecordsAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => string.Equals(
                NormalizePath(record.SourceProjectPath),
                normalizedProjectPath,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(record => record.CreatedAtUtc)
            .ToArray();
    }

    public async Task DeleteBackupAsync(
        ProjectBackupRecord backup,
        CancellationToken cancellationToken = default)
    {
        var backupPath = NormalizePath(backup.BackupPath);
        if (Directory.Exists(backupPath))
        {
            await ValidateBackupAsync(backup, cancellationToken).ConfigureAwait(false);
            if ((File.GetAttributes(backupPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("A backup stored through a directory link cannot be deleted by FluenityHub.");
            }

            if (IsRootPath(backupPath)
                || string.Equals(backupPath, NormalizePath(backup.SourceProjectPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The backup path is not safe to delete.");
            }

            await Task.Run(
                () => Directory.Delete(backupPath, recursive: true),
                cancellationToken).ConfigureAwait(false);
        }

        await RegistryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
            records.RemoveAll(record => string.Equals(record.Id, backup.Id, StringComparison.OrdinalIgnoreCase));
            await SaveRecordsCoreAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RegistryGate.Release();
        }
    }

    public static string GetValidationError(string sourcePath, string destinationRoot, string folderName)
    {
        if (!Directory.Exists(sourcePath))
        {
            return "The source project folder no longer exists.";
        }

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            return "Choose a destination folder.";
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "Enter a folder name.";
        }

        if (folderName is "." or ".."
            || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || folderName.EndsWith(' ')
            || folderName.EndsWith('.'))
        {
            return "The folder name contains characters or an ending that Windows does not allow.";
        }

        var baseName = folderName.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(baseName))
        {
            return "Choose a different folder name. This name is reserved by Windows.";
        }

        try
        {
            var targetPath = Path.Combine(destinationRoot.Trim(), folderName.Trim());
            ValidateTargetPath(sourcePath, targetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        {
            return ex.Message;
        }

        return string.Empty;
    }

    private static async Task<T> CopyProjectAsync<T>(
        string sourceProjectPath,
        string targetPath,
        bool includeUserSettings,
        bool includeGitHistory,
        bool skipManifest,
        IProgress<ProjectCopyProgress>? progress,
        CancellationToken cancellationToken,
        Func<long, string, Task<T>> beforeCommit)
    {
        var sourceRoot = NormalizePath(sourceProjectPath);
        var finalTarget = NormalizePath(targetPath);
        var targetParent = Path.GetDirectoryName(finalTarget)
            ?? throw new InvalidOperationException("The destination folder is invalid.");
        Directory.CreateDirectory(targetParent);

        var stagingPath = Path.Combine(
            targetParent,
            $".{Path.GetFileName(finalTarget)}.fluenity-{Guid.NewGuid():N}.tmp");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProjectCopyProgress("Preparing files", string.Empty, 0, 0, 0, 0));

            var files = await Task.Run(
                () => EnumerateFiles(
                    sourceRoot,
                    includeUserSettings,
                    includeGitHistory,
                    skipManifest,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var totalBytes = files.Sum(file => file.Length);
            Directory.CreateDirectory(stagingPath);

            long copiedBytes = 0;
            var copiedFiles = 0;
            var buffer = new byte[BufferSize];
            long lastProgressReportAt = 0;

            void ReportCopyProgress(string relativePath)
            {
                if (progress is null)
                {
                    return;
                }

                var now = Environment.TickCount64;
                if (lastProgressReportAt != 0 && now - lastProgressReportAt < 100)
                {
                    return;
                }

                lastProgressReportAt = now;
                progress.Report(new ProjectCopyProgress(
                    "Copying project files",
                    relativePath,
                    copiedBytes,
                    totalBytes,
                    copiedFiles,
                    files.Count));
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationFile = Path.Combine(stagingPath, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

                await using var source = new FileStream(
                    file.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var destination = new FileStream(
                    destinationFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    copiedBytes += bytesRead;
                    ReportCopyProgress(file.RelativePath);
                }

                File.SetLastWriteTimeUtc(destinationFile, file.LastWriteTimeUtc);
                copiedFiles++;
                ReportCopyProgress(file.RelativePath);
            }

            var result = await beforeCommit(totalBytes, stagingPath).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ProjectCopyProgress(
                "Finishing",
                string.Empty,
                totalBytes,
                totalBytes,
                files.Count,
                files.Count));

            if (Directory.Exists(finalTarget))
            {
                throw new IOException("A folder with this name was created while the operation was running.");
            }

            Directory.Move(stagingPath, finalTarget);
            return result;
        }
        catch
        {
            TryDeleteStagingDirectory(stagingPath, targetParent);
            throw;
        }
    }

    private static List<ProjectSourceFile> EnumerateFiles(
        string sourceRoot,
        bool includeUserSettings,
        bool includeGitHistory,
        bool skipManifest,
        CancellationToken cancellationToken)
    {
        var result = new List<ProjectSourceFile>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var relativeDirectory = Path.GetRelativePath(sourceRoot, directory);

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new DirectoryInfo(childDirectory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var childRelative = Path.GetRelativePath(sourceRoot, childDirectory);
                if (IsExcludedRootDirectory(childRelative, includeUserSettings, includeGitHistory))
                {
                    continue;
                }

                pending.Push(childDirectory);
            }

            foreach (var filePath in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(filePath);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceRoot, filePath);
                if (skipManifest
                    && string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new ProjectSourceFile(
                    filePath,
                    relativePath,
                    file.Length,
                    file.LastWriteTimeUtc));
            }
        }

        return result;
    }

    private static bool IsExcludedRootDirectory(
        string relativePath,
        bool includeUserSettings,
        bool includeGitHistory)
    {
        if (relativePath.Contains(Path.DirectorySeparatorChar)
            || relativePath.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        if (AlwaysExcludedRootDirectories.Contains(relativePath))
        {
            return true;
        }

        if (!includeUserSettings
            && string.Equals(relativePath, "UserSettings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !includeGitHistory
               && string.Equals(relativePath, ".git", StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddRecordAsync(ProjectBackupRecord record, CancellationToken cancellationToken)
    {
        await RegistryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
            records.RemoveAll(existing => string.Equals(existing.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            records.Add(record);
            await SaveRecordsCoreAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RegistryGate.Release();
        }
    }

    private async Task<List<ProjectBackupRecord>> LoadRecordsAsync(CancellationToken cancellationToken)
    {
        await RegistryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadRecordsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RegistryGate.Release();
        }
    }

    private async Task<List<ProjectBackupRecord>> LoadRecordsCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_registryPath))
        {
            return [];
        }

        await using var stream = new FileStream(
            _registryPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync(
                   stream,
                   AppJsonContext.Default.ListProjectBackupRecord,
                   cancellationToken).ConfigureAwait(false)
               ?? [];
    }

    private async Task SaveRecordsCoreAsync(
        List<ProjectBackupRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".project-backups.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    records,
                    AppJsonContext.Default.ListProjectBackupRecord,
                    cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _registryPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task WriteManifestAsync(
        ProjectBackupRecord record,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = record.Id,
            ["sourceProjectPath"] = record.SourceProjectPath,
            ["projectTitle"] = record.ProjectTitle,
            ["unityVersion"] = record.UnityVersion,
            ["createdAtUtc"] = record.CreatedAtUtc.ToString("O"),
            ["totalBytes"] = record.TotalBytes,
            ["includesUserSettings"] = record.IncludesUserSettings,
            ["includesGitHistory"] = record.IncludesGitHistory
        };

        var manifestPath = Path.Combine(backupDirectory, ManifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(AppJsonContext.Default.Options),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateBackupAsync(
        ProjectBackupRecord backup,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(backup.BackupPath))
        {
            throw new DirectoryNotFoundException("The backup folder no longer exists.");
        }

        var manifestPath = Path.Combine(backup.BackupPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("This folder is not a FluenityHub project backup.");
        }

        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonNode.Parse(manifestText)?.AsObject();
        var manifestId = manifest?["id"]?.GetValue<string>();
        if (!string.Equals(manifestId, backup.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The backup manifest does not match the selected backup.");
        }
    }

    private static void ValidateUnityProject(string projectPath)
    {
        if (!Directory.Exists(projectPath)
            || !File.Exists(Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt")))
        {
            throw new InvalidDataException("The selected folder is not a valid Unity project.");
        }
    }

    private static void ValidateTargetPath(string sourcePath, string targetPath)
    {
        var source = NormalizePath(sourcePath);
        var target = NormalizePath(targetPath);

        if (IsRootPath(target))
        {
            throw new InvalidOperationException("Choose a folder inside a drive, not the drive root.");
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The destination cannot be the source project folder.");
        }

        if (IsPathInside(target, source))
        {
            throw new InvalidOperationException("The destination cannot be inside the source project.");
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException("A file or folder with this name already exists.");
        }
    }

    private static bool IsPathInside(string candidatePath, string parentPath)
    {
        var candidate = NormalizePath(candidatePath) + Path.DirectorySeparatorChar;
        var parent = NormalizePath(parentPath) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return string.Equals(
            fullPath,
            Path.GetPathRoot(fullPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static void TryDeleteStagingDirectory(string stagingPath, string expectedParent)
    {
        try
        {
            var normalizedStaging = NormalizePath(stagingPath);
            var normalizedParent = NormalizePath(expectedParent);
            if (Directory.Exists(normalizedStaging)
                && IsPathInside(normalizedStaging, normalizedParent)
                && Path.GetFileName(normalizedStaging).Contains(".fluenity-", StringComparison.Ordinal))
            {
                Directory.Delete(normalizedStaging, recursive: true);
            }
        }
        catch
        {
            // The failed operation is already reported; staging cleanup is best effort.
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Atomic registry write has either completed or already failed.
        }
    }

    private sealed record ProjectSourceFile(
        string FullPath,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);
}
