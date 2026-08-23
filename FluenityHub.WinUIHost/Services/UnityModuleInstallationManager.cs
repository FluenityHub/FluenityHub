using Microsoft.UI.Dispatching;
using System.Text;
using System.Text.Json;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public enum UnityModuleInstallationState
{
    Queued,
    Preparing,
    DownloadingCli,
    InstallingEditor,
    InstallingModules,
    Pausing,
    Paused,
    Canceling,
    Succeeded,
    Failed,
    Canceled
}

public enum UnityModuleOperationKind
{
    Install,
    Repair,
    Remove
}

public sealed record UnityModuleInstallationTarget(
    string Id,
    string Name,
    long DownloadSizeBytes);

public sealed record UnityModuleInstallationRequest(
    string EditorVersion,
    string EditorInstallDirectory,
    IReadOnlyList<UnityModuleInstallationTarget> Modules,
    UnityCliReleaseInfo? CliRelease)
{
    public bool InstallsEditor { get; init; }
    public UnityModuleOperationKind OperationKind { get; init; } = UnityModuleOperationKind.Install;
    public bool ResumeInterruptedDownload { get; init; }
    public string? EditorRevision { get; init; }
    public IReadOnlyList<string> ModuleIds => Modules.Select(module => module.Id).ToArray();
    public int ModuleCount => Modules.Count;
    public long DownloadSizeBytes => Modules.Sum(module => module.DownloadSizeBytes);
}

public sealed record UnityModuleProgressSnapshot(
    string Id,
    string Name,
    string Phase,
    string Message,
    double? Percentage,
    long? BytesReceived,
    long? TotalBytes,
    bool IsDependency,
    bool IsCompleted,
    bool HasError);

public sealed record UnityModuleInstallationSnapshot(
    Guid Id,
    string EditorVersion,
    string EditorInstallDirectory,
    UnityModuleInstallationState State,
    string Phase,
    string Message,
    double? Percentage,
    long? BytesReceived,
    long? TotalBytes,
    int ModuleCount,
    long DownloadSizeBytes,
    bool InstallsEditor,
    UnityModuleOperationKind OperationKind,
    IReadOnlyList<UnityModuleProgressSnapshot> Modules,
    string? LogFilePath,
    string? ErrorDetails)
{
    public bool IsTerminal => State is
        UnityModuleInstallationState.Succeeded
        or UnityModuleInstallationState.Failed
        or UnityModuleInstallationState.Canceled;

    public bool CanCancel => State is
        UnityModuleInstallationState.Queued
        or UnityModuleInstallationState.Preparing
        or UnityModuleInstallationState.DownloadingCli
        or UnityModuleInstallationState.InstallingEditor
        or UnityModuleInstallationState.InstallingModules
        or UnityModuleInstallationState.Paused;

    public bool CanPause => OperationKind != UnityModuleOperationKind.Remove && State is
        UnityModuleInstallationState.Queued
        or UnityModuleInstallationState.Preparing
        or UnityModuleInstallationState.DownloadingCli
        or UnityModuleInstallationState.InstallingEditor
        or UnityModuleInstallationState.InstallingModules;

    public bool CanResume => State == UnityModuleInstallationState.Paused;
}

internal sealed record PersistedUnityInstallation(
    Guid Id,
    string EditorVersion,
    string EditorInstallDirectory,
    List<UnityModuleInstallationTarget> Modules,
    bool InstallsEditor,
    string? EditorRevision,
    UnityModuleOperationKind OperationKind = UnityModuleOperationKind.Install);

public sealed record UnityModuleInstallationEnqueueResult(
    bool Accepted,
    string Message,
    UnityModuleInstallationSnapshot? Operation);

public sealed class UnityModuleInstallationChangedEventArgs : EventArgs
{
    public UnityModuleInstallationChangedEventArgs(
        UnityModuleInstallationSnapshot? changedOperation,
        UnityModuleInstallationSnapshot? currentOperation,
        int queuedCount)
    {
        ChangedOperation = changedOperation;
        CurrentOperation = currentOperation;
        QueuedCount = queuedCount;
    }

    public UnityModuleInstallationSnapshot? ChangedOperation { get; }
    public UnityModuleInstallationSnapshot? CurrentOperation { get; }
    public int QueuedCount { get; }
}

public sealed class UnityModuleInstallationManager
{
    private const int RetainedOperationCount = 5;

    private sealed class ModuleJob
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required long DownloadSizeBytes { get; init; }
        public bool IsDependency { get; init; }
        public string Phase { get; set; } = "Queued";
        public string Message { get; set; } = "Waiting to download.";
        public double? Percentage { get; set; }
        public long? BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
        public bool IsCompleted { get; set; }
        public bool HasError { get; set; }

        public UnityModuleProgressSnapshot CreateSnapshot()
            => new(
                Id,
                Name,
                Phase,
                Message,
                Percentage,
                BytesReceived,
                TotalBytes,
                IsDependency,
                IsCompleted,
                HasError);
    }

    private sealed class InstallationJob
    {
        public required Guid Id { get; init; }
        public required UnityModuleInstallationRequest Request { get; set; }
        public required List<ModuleJob> Modules { get; init; }
        public CancellationTokenSource Cancellation { get; set; } = new();
        public bool PauseRequested { get; set; }
        public UnityModuleInstallationState State { get; set; } = UnityModuleInstallationState.Queued;
        public string Phase { get; set; } = "Queued";
        public string Message { get; set; } = "Waiting for the current installation to finish.";
        public double? Percentage { get; set; }
        public long? BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
        public string? LogFilePath { get; set; }
        public string? ErrorDetails { get; set; }
        public object LogSync { get; } = new();

        public UnityModuleInstallationSnapshot CreateSnapshot()
            => new(
                Id,
                Request.EditorVersion,
                Request.EditorInstallDirectory,
                State,
                Phase,
                Message,
                Percentage,
                BytesReceived,
                TotalBytes,
                Request.ModuleCount,
                Request.DownloadSizeBytes,
                Request.InstallsEditor,
                Request.OperationKind,
                Modules.Select(module => module.CreateSnapshot()).ToArray(),
                LogFilePath,
                ErrorDetails);
    }

    private readonly object _sync = new();
    private readonly List<InstallationJob> _queue = [];
    private readonly List<InstallationJob> _recent = [];
    private readonly HashSet<Guid> _dismissedOperationIds = [];
    private readonly UnityCliToolService _cliToolService = new();
    private readonly UnityEditorInstallationService _editorInstallationService = new();
    private readonly UnityModuleService _moduleService = new();
    private InstallationJob? _current;
    private DispatcherQueue? _dispatcherQueue;
    private bool _processorRunning;
    private static readonly string PersistencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluenityHub",
        "InstallationQueue.json");

    private UnityModuleInstallationManager()
    {
        LoadPersistedOperations();
    }

    public static UnityModuleInstallationManager Instance { get; } = new();

    public event EventHandler<UnityModuleInstallationChangedEventArgs>? OperationChanged;

    public void AttachDispatcher(DispatcherQueue dispatcherQueue)
        => _dispatcherQueue = dispatcherQueue;

    public UnityModuleInstallationSnapshot? CurrentOperation
    {
        get
        {
            lock (_sync)
            {
                return _current?.CreateSnapshot();
            }
        }
    }

    public int QueuedCount
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public IReadOnlyList<UnityModuleInstallationSnapshot> ActiveOperations
    {
        get
        {
            lock (_sync)
            {
                var operations = new List<UnityModuleInstallationSnapshot>();
                if (_current is not null && !_current.CreateSnapshot().IsTerminal)
                {
                    operations.Add(_current.CreateSnapshot());
                }

                operations.AddRange(_queue.Select(job => job.CreateSnapshot()));
                return operations;
            }
        }
    }

    public IReadOnlyList<UnityModuleInstallationSnapshot> VisibleOperations
    {
        get
        {
            lock (_sync)
            {
                var operations = new List<UnityModuleInstallationSnapshot>();
                if (_current is not null
                    && _current.State != UnityModuleInstallationState.Succeeded
                    && !_dismissedOperationIds.Contains(_current.Id))
                {
                    operations.Add(_current.CreateSnapshot());
                }

                operations.AddRange(_queue.Select(job => job.CreateSnapshot()));
                operations.AddRange(
                    _recent
                        .Where(job => job.State != UnityModuleInstallationState.Succeeded)
                        .Where(job => !_dismissedOperationIds.Contains(job.Id))
                        .Where(job => operations.All(item => item.Id != job.Id))
                        .Select(job => job.CreateSnapshot()));
                return operations;
            }
        }
    }

    public bool HasActiveOperations
    {
        get
        {
            lock (_sync)
            {
                return _current is not null || _queue.Count > 0;
            }
        }
    }

    public bool IsEditorBusy(string editorVersion)
    {
        lock (_sync)
        {
            return IsSameEditor(_current?.Request.EditorVersion, editorVersion)
                || _queue.Any(job => IsSameEditor(job.Request.EditorVersion, editorVersion));
        }
    }

    public bool IsEditorPathBusy(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        lock (_sync)
        {
            return IsPathInsideEditor(fullPath, _current?.Request.EditorInstallDirectory)
                || _queue.Any(job => IsPathInsideEditor(fullPath, job.Request.EditorInstallDirectory));
        }
    }

    public UnityModuleInstallationEnqueueResult Enqueue(UnityModuleInstallationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Modules.Count == 0)
        {
            return new(false, "Select at least one component to install.", null);
        }

        InstallationJob job;
        var startProcessor = false;
        lock (_sync)
        {
            if ((_current is { } current
                    && !current.CreateSnapshot().IsTerminal
                    && IsSameEditor(current.Request.EditorVersion, request.EditorVersion))
                || _queue.Any(item => IsSameEditor(item.Request.EditorVersion, request.EditorVersion)))
            {
                return new(
                    false,
                    $"An installation is already queued or running for Unity {request.EditorVersion}.",
                    null);
            }

            job = new InstallationJob
            {
                Id = Guid.NewGuid(),
                Request = request,
                Modules = request.Modules.Select(module => new ModuleJob
                {
                    Id = module.Id,
                    Name = module.Name,
                    DownloadSizeBytes = module.DownloadSizeBytes
                }).ToList(),
                Message = _current is null
                    ? GetPreparingMessage(request)
                    : "Waiting for the current operation to finish."
            };
            _queue.Add(job);

            if (!_processorRunning)
            {
                _processorRunning = true;
                startProcessor = true;
            }
        }

        Publish(job);
        PersistRecoverableOperations();
        if (startProcessor)
        {
            _ = ProcessQueueAsync();
        }

        return new(
            true,
            _current is null
                ? GetStartedMessage(request)
                : GetQueuedMessage(request),
            job.CreateSnapshot());
    }

    public bool Cancel(Guid operationId)
    {
        InstallationJob? job;
        var cancelRunningJob = false;
        lock (_sync)
        {
            if (_current?.Id == operationId)
            {
                job = _current;
                if (job.State is UnityModuleInstallationState.Canceling || job.CreateSnapshot().IsTerminal)
                {
                    return false;
                }

                job.State = UnityModuleInstallationState.Canceling;
                job.Phase = "Canceling";
                job.Message = job.Request.OperationKind == UnityModuleOperationKind.Remove
                    ? "Stopping module removal."
                    : "Stopping Unity CLI and active downloads.";
                job.Percentage = null;
                job.BytesReceived = null;
                job.TotalBytes = null;
                foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                {
                    module.Phase = "Canceling";
                    module.Message = job.Request.OperationKind == UnityModuleOperationKind.Remove
                        ? "Stopping removal."
                        : "Stopping download and installation.";
                }

                cancelRunningJob = true;
            }
            else
            {
                job = _queue.FirstOrDefault(item => item.Id == operationId);
                job ??= _recent.FirstOrDefault(item =>
                    item.Id == operationId
                    && item.State == UnityModuleInstallationState.Paused);
                if (job is null)
                {
                    return false;
                }

                _queue.Remove(job);
                _recent.Remove(job);
                job.State = UnityModuleInstallationState.Canceled;
                job.Phase = "Canceled";
                job.Message = "The queued operation was canceled.";
                job.Percentage = null;
                foreach (var module in job.Modules)
                {
                    module.Phase = "Canceled";
                    module.Message = "The operation did not start.";
                }

                AddRecentLocked(job);
            }
        }

        PersistRecoverableOperations();

        Publish(job);
        if (cancelRunningJob)
        {
            job.Cancellation.Cancel();
        }
        else
        {
            job.Cancellation.Dispose();
        }

        return true;
    }

    public bool Pause(Guid operationId)
    {
        InstallationJob? job;
        var pauseRunningJob = false;
        lock (_sync)
        {
            if (_current?.Id == operationId)
            {
                job = _current;
                if (!job.CreateSnapshot().CanPause)
                {
                    return false;
                }

                job.PauseRequested = true;
                job.State = UnityModuleInstallationState.Pausing;
                job.Phase = "Pausing";
                job.Message = "Stopping safely after preserving downloaded data.";
                foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                {
                    module.Phase = "Pausing";
                    module.Message = "Preserving downloaded data.";
                }

                pauseRunningJob = true;
            }
            else
            {
                job = _queue.FirstOrDefault(item => item.Id == operationId);
                if (job is null)
                {
                    return false;
                }

                _queue.Remove(job);
                job.State = UnityModuleInstallationState.Paused;
                job.Phase = "Paused";
                job.Message = "This installation is paused.";
                foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                {
                    module.Phase = "Paused";
                    module.Message = "Waiting to resume.";
                }
                AddRecentLocked(job);
            }
        }

        Publish(job);
        PersistRecoverableOperations();
        if (pauseRunningJob)
        {
            job.Cancellation.Cancel();
        }
        else
        {
            job.Cancellation.Dispose();
        }

        return true;
    }

    public UnityModuleInstallationEnqueueResult Resume(Guid operationId)
    {
        InstallationJob? job;
        var startProcessor = false;
        lock (_sync)
        {
            job = _recent.FirstOrDefault(item =>
                item.Id == operationId
                && item.State == UnityModuleInstallationState.Paused);
            if (job is null)
            {
                return new(false, "Only paused operations can be resumed.", null);
            }

            if ((_current is not null
                    && IsSameEditor(_current.Request.EditorVersion, job.Request.EditorVersion))
                || _queue.Any(item => IsSameEditor(item.Request.EditorVersion, job.Request.EditorVersion)))
            {
                return new(false, $"Another module operation is active for Unity {job.Request.EditorVersion}.", null);
            }

            _recent.Remove(job);
            job.Request = job.Request with { ResumeInterruptedDownload = true };
            job.Cancellation = new CancellationTokenSource();
            job.PauseRequested = false;
            job.State = UnityModuleInstallationState.Queued;
            job.Phase = "Queued";
            job.Message = _current is null
                ? "Resuming from downloaded data."
                : "Waiting to resume from downloaded data.";
            job.ErrorDetails = null;
            foreach (var module in job.Modules.Where(module => !module.IsCompleted))
            {
                module.Phase = "Queued";
                module.Message = "Waiting to resume.";
                module.HasError = false;
            }
            _queue.Add(job);

            if (!_processorRunning)
            {
                _processorRunning = true;
                startProcessor = true;
            }
        }

        Publish(job);
        PersistRecoverableOperations();
        if (startProcessor)
        {
            _ = ProcessQueueAsync();
        }

        return new(true, $"Resuming Unity {job.Request.EditorVersion} from cached data.", job.CreateSnapshot());
    }

    public UnityModuleInstallationEnqueueResult Retry(Guid operationId)
    {
        UnityModuleInstallationRequest? request;
        lock (_sync)
        {
            request = (_current?.Id == operationId
                    && _current.State is UnityModuleInstallationState.Failed or UnityModuleInstallationState.Canceled
                        ? _current
                        : _recent.FirstOrDefault(job =>
                            job.Id == operationId
                            && job.State is UnityModuleInstallationState.Failed or UnityModuleInstallationState.Canceled))?.Request;
        }

        if (request is null)
        {
            return new(false, "Only failed or canceled operations can be retried.", null);
        }

        var result = Enqueue(request with { ResumeInterruptedDownload = true });
        if (result.Accepted)
        {
            Dismiss(operationId);
        }

        return result;
    }

    public bool Dismiss(Guid operationId)
    {
        var removed = false;
        lock (_sync)
        {
            var isTerminalCurrent = _current?.Id == operationId
                && _current.CreateSnapshot().IsTerminal;
            var removedFromRecent = _recent.RemoveAll(job =>
                job.Id == operationId
                && job.CreateSnapshot().IsTerminal) > 0;
            removed = isTerminalCurrent || removedFromRecent;
            if (removed)
            {
                _dismissedOperationIds.Add(operationId);
            }
        }

        if (removed)
        {
            PublishSnapshot(null);
        }

        return removed;
    }

    public int DismissInactive()
    {
        var dismissedCount = 0;
        lock (_sync)
        {
            var inactiveIds = _recent
                .Where(job => job.State is UnityModuleInstallationState.Failed or UnityModuleInstallationState.Canceled)
                .Select(job => job.Id)
                .ToList();

            if (_current is { } current
                && current.State is UnityModuleInstallationState.Failed or UnityModuleInstallationState.Canceled)
            {
                inactiveIds.Add(current.Id);
            }

            foreach (var operationId in inactiveIds)
            {
                if (_dismissedOperationIds.Add(operationId))
                {
                    dismissedCount++;
                }
            }
        }

        if (dismissedCount > 0)
        {
            PublishSnapshot(null);
        }

        return dismissedCount;
    }

    private static string GetPreparingMessage(UnityModuleInstallationRequest request)
        => request.OperationKind switch
        {
            UnityModuleOperationKind.Repair => $"Preparing to repair {request.Modules[0].Name}.",
            UnityModuleOperationKind.Remove => $"Preparing to remove {request.Modules[0].Name}.",
            _ when request.InstallsEditor => $"Preparing Unity {request.EditorVersion}.",
            _ => $"Preparing {request.ModuleCount} selected module{(request.ModuleCount == 1 ? string.Empty : "s")}."
        };

    private static string GetStartedMessage(UnityModuleInstallationRequest request)
        => request.OperationKind switch
        {
            UnityModuleOperationKind.Repair => $"Repairing {request.Modules[0].Name} in the background.",
            UnityModuleOperationKind.Remove => $"Removing {request.Modules[0].Name} in the background.",
            _ when request.InstallsEditor => $"Installing Unity {request.EditorVersion} in the background.",
            _ => $"Installing modules for Unity {request.EditorVersion} in the background."
        };

    private static string GetQueuedMessage(UnityModuleInstallationRequest request)
        => request.OperationKind switch
        {
            UnityModuleOperationKind.Repair => $"Queued {request.Modules[0].Name} for repair.",
            UnityModuleOperationKind.Remove => $"Queued {request.Modules[0].Name} for removal.",
            _ when request.InstallsEditor => $"Queued Unity {request.EditorVersion} for installation.",
            _ => $"Queued modules for Unity {request.EditorVersion}."
        };

    private void CompleteModuleOperation(
        InstallationJob job,
        UnityModuleInstallResult result,
        string successPhase,
        string successMessage,
        string completedModulePhase,
        string completedModuleMessage)
    {
        if (!result.Succeeded)
        {
            lock (_sync)
            {
                job.ErrorDetails = result.Message;
                foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                {
                    module.HasError = true;
                    module.Phase = "Failed";
                    module.Message = result.Message;
                }
            }

            UpdateJob(
                job,
                UnityModuleInstallationState.Failed,
                job.Request.OperationKind == UnityModuleOperationKind.Remove
                    ? "Removal failed"
                    : "Repair failed",
                result.Message,
                job.Percentage);
            return;
        }

        lock (_sync)
        {
            foreach (var module in job.Modules)
            {
                module.Phase = completedModulePhase;
                module.Message = completedModuleMessage;
                module.Percentage = 100;
                module.IsCompleted = true;
                module.HasError = false;
            }
        }

        UpdateJob(
            job,
            UnityModuleInstallationState.Succeeded,
            successPhase,
            successMessage,
            percentage: 100);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            InstallationJob? job;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    _current = null;
                    _processorRunning = false;
                    job = null;
                }
                else
                {
                    job = _queue[0];
                    _queue.RemoveAt(0);
                    _current = job;
                }
            }

            if (job is null)
            {
                Publish(null);
                return;
            }

            try
            {
                UpdateJob(
                    job,
                    UnityModuleInstallationState.Preparing,
                    job.Request.OperationKind switch
                    {
                        UnityModuleOperationKind.Repair => "Preparing repair",
                        UnityModuleOperationKind.Remove => "Preparing removal",
                        _ => "Preparing installation"
                    },
                    GetPreparingMessage(job.Request),
                    percentage: null);
                job.LogFilePath = await CreateLogAsync(job);
                Publish(job);

                var cancellationToken = job.Cancellation.Token;
                cancellationToken.ThrowIfCancellationRequested();

                if (job.Request.OperationKind == UnityModuleOperationKind.Remove)
                {
                    var target = job.Request.Modules.Single();
                    UpdateJob(
                        job,
                        UnityModuleInstallationState.InstallingModules,
                        "Removing module",
                        $"Removing {target.Name} from Unity {job.Request.EditorVersion}.",
                        percentage: 0);
                    var removalProgress = new Progress<UnityModuleInstallProgress>(
                        progress => UpdateModuleProgress(job, progress));
                    var removalResult = await _moduleService.RemoveAsync(
                        job.Request.EditorInstallDirectory,
                        target.Id,
                        removalProgress,
                        line => AppendLogLine(job, line),
                        cancellationToken);
                    AppendLogLine(job, $"Result: {removalResult.Message}");
                    CompleteModuleOperation(
                        job,
                        removalResult,
                        successPhase: "Removal complete",
                        successMessage: removalResult.Message,
                        completedModulePhase: "Removed",
                        completedModuleMessage: "Removed successfully.");
                    continue;
                }

                var cliStatus = _cliToolService.GetStatus();
                var release = job.Request.CliRelease;
                if (release is null)
                {
                    try
                    {
                        UpdateJob(
                            job,
                            UnityModuleInstallationState.Preparing,
                            "Checking Unity CLI",
                            "Checking for command-line component updates.",
                            percentage: null);
                        release = await _cliToolService.GetLatestReleaseAsync(cancellationToken);
                    }
                    catch (Exception ex)
                        when (cliStatus.IsInstalled && ex is not OperationCanceledException)
                    {
                        // An offline update check must not prevent use of a verified local CLI.
                    }
                }

                var cliNeedsInstall = !cliStatus.IsInstalled
                    || (release is not null
                        && UnityCliToolService.IsReleaseNewer(cliStatus.Version, release.Version));
                if (cliNeedsInstall)
                {
                    release ??= await _cliToolService.GetLatestReleaseAsync(cancellationToken);
                    var cliProgress = new Progress<UnityCliDownloadProgress>(progress =>
                    {
                        UpdateJob(
                            job,
                            UnityModuleInstallationState.DownloadingCli,
                            cliStatus.IsInstalled ? "Updating Unity CLI" : "Downloading Unity CLI",
                            progress.TotalBytes is > 0
                                ? $"{FormatBytes(progress.BytesReceived)} of {FormatBytes(progress.TotalBytes.Value)}"
                                : FormatBytes(progress.BytesReceived),
                            progress.Percentage,
                            progress.BytesReceived,
                            progress.TotalBytes);
                    });

                    UpdateJob(
                        job,
                        UnityModuleInstallationState.DownloadingCli,
                        cliStatus.IsInstalled ? "Updating Unity CLI" : "Downloading Unity CLI",
                        "Downloading and verifying the command-line component.",
                        percentage: null);
                    await _cliToolService.InstallAsync(release, cliProgress, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (job.Request.InstallsEditor)
                {
                    // Clean stale cached installers from previous versions to
                    // reclaim disk space and avoid corrupt file reuse.
                    try
                    {
                        var activeVersions = new UnityEditorLocator()
                            .GetInstalledEditors()
                            .Keys
                            .Append(job.Request.EditorVersion)
                            .ToArray();
                        var reclaimed = UnityEditorInstallationService.CleanStaleCachedInstallers(
                            job.Request.EditorInstallDirectory,
                            activeVersions,
                            line => AppendLogLine(job, line));
                        if (reclaimed > 0)
                        {
                            AppendLogLine(job,
                                $"Cleaned {FormatBytes(reclaimed)} of stale cached installers before installation.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLogLine(job,
                            $"Stale cache cleanup was skipped: {ex.Message}");
                    }

                    var editorExecutablePath = Path.Combine(
                        job.Request.EditorInstallDirectory,
                        "Editor",
                        "Unity.exe");
                    var requestedModules = job.Request.Modules
                        .Where(module => !module.Id.Equals(
                            "unity-editor",
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    // A previous Editor+modules command can finish the Editor
                    // before a module download, UAC prompt, or installer fails.
                    // Retrying the Editor command wastes the completed install,
                    // and --resume can be a successful no-op. Continue through
                    // the documented install-modules flow for only the remaining
                    // requested components.
                    if (File.Exists(editorExecutablePath))
                    {
                        AppendLogLine(
                            job,
                            $"Unity {job.Request.EditorVersion} is already installed. Continuing with {requestedModules.Length} requested module{(requestedModules.Length == 1 ? string.Empty : "s")}.");

                        lock (_sync)
                        {
                            var editorComponent = job.Modules.FirstOrDefault(module =>
                                module.Id.Equals("unity-editor", StringComparison.OrdinalIgnoreCase));
                            if (editorComponent is not null)
                            {
                                editorComponent.Phase = "Installed";
                                editorComponent.Message = "Editor installation already completed.";
                                editorComponent.Percentage = 100;
                                editorComponent.IsCompleted = true;
                                editorComponent.HasError = false;
                            }
                        }

                        if (requestedModules.Length == 0)
                        {
                            UpdateJob(
                                job,
                                UnityModuleInstallationState.Succeeded,
                                "Installation complete",
                                $"Unity {job.Request.EditorVersion} is installed.",
                                percentage: 100);
                            continue;
                        }

                        lock (_sync)
                        {
                            foreach (var component in job.Modules.Where(module =>
                                         !module.Id.Equals("unity-editor", StringComparison.OrdinalIgnoreCase)))
                            {
                                component.Phase = "Waiting";
                                component.Message = "Waiting for Unity CLI to schedule this module.";
                                component.Percentage = null;
                                component.IsCompleted = false;
                                component.HasError = false;
                            }
                        }

                        UpdateJob(
                            job,
                            UnityModuleInstallationState.InstallingModules,
                            "Resolving modules",
                            $"Unity CLI is scheduling {requestedModules.Length} remaining module{(requestedModules.Length == 1 ? string.Empty : "s")}.",
                            percentage: null);
                        var remainingProgress = new Progress<UnityModuleInstallProgress>(
                            progress => UpdateModuleProgress(job, progress));
                        var remainingResult = await _moduleService.InstallAsync(
                            job.Request.EditorVersion,
                            job.Request.EditorInstallDirectory,
                            requestedModules.Select(module => module.Id).ToArray(),
                            remainingProgress,
                            line => AppendLogLine(job, line),
                            cancellationToken);
                        AppendLogLine(job, $"Result: {remainingResult.Message}");

                        if (!remainingResult.Succeeded)
                        {
                            lock (_sync)
                            {
                                job.ErrorDetails = remainingResult.Message;
                                foreach (var component in job.Modules.Where(module =>
                                             !module.Id.Equals("unity-editor", StringComparison.OrdinalIgnoreCase)
                                             && !module.IsCompleted))
                                {
                                    component.HasError = true;
                                    component.Phase = "Failed";
                                    component.Message = remainingResult.Message;
                                }
                            }

                            UpdateJob(
                                job,
                                UnityModuleInstallationState.Failed,
                                "Module installation failed",
                                remainingResult.Message,
                                job.Percentage);
                            continue;
                        }

                        lock (_sync)
                        {
                            foreach (var component in job.Modules)
                            {
                                component.Phase = "Installed";
                                component.Message = "Installed successfully.";
                                component.Percentage = 100;
                                component.IsCompleted = true;
                                component.HasError = false;
                            }
                        }

                        UpdateJob(
                            job,
                            UnityModuleInstallationState.Succeeded,
                            "Installation complete",
                            $"Installed the selected modules for Unity {job.Request.EditorVersion}.",
                            percentage: 100);
                        continue;
                    }

                    lock (_sync)
                    {
                        foreach (var component in job.Modules)
                        {
                            component.Phase = "Waiting";
                            component.Message = "Waiting for Unity CLI to resolve the Editor package.";
                        }
                    }

                    UpdateJob(
                        job,
                        UnityModuleInstallationState.InstallingEditor,
                        "Resolving Editor",
                        $"Unity CLI is resolving Unity {job.Request.EditorVersion}.",
                        percentage: null);
                    var editorProgress = new Progress<UnityModuleInstallProgress>(
                        progress => UpdateModuleProgress(job, progress));
                    var installRoot = Path.GetDirectoryName(
                        Path.TrimEndingDirectorySeparator(job.Request.EditorInstallDirectory))
                        ?? job.Request.EditorInstallDirectory;
                    var editorResult = await _editorInstallationService.InstallAsync(
                        job.Request.EditorVersion,
                        installRoot,
                        job.Request.EditorRevision,
                        job.Request.ModuleIds,
                        job.Request.ResumeInterruptedDownload,
                        editorProgress,
                        line => AppendLogLine(job, line),
                        cancellationToken);

                    AppendLogLine(job, $"Result: {editorResult.Message}");
                    if (!editorResult.Succeeded)
                    {
                        lock (_sync)
                        {
                            job.ErrorDetails = editorResult.Message;
                            foreach (var component in job.Modules)
                            {
                                component.HasError = true;
                                component.Phase = "Failed";
                                component.Message = editorResult.Message;
                            }
                        }

                        UpdateJob(
                            job,
                            UnityModuleInstallationState.Failed,
                            "Installation failed",
                            editorResult.Message,
                            job.Percentage);
                        continue;
                    }

                    lock (_sync)
                    {
                        foreach (var component in job.Modules)
                        {
                            component.Phase = "Installed";
                            component.Message = "Installed successfully.";
                            component.Percentage = 100;
                            component.IsCompleted = true;
                            component.HasError = false;
                        }
                    }

                    UpdateJob(
                        job,
                        UnityModuleInstallationState.Succeeded,
                        "Installation complete",
                        $"Installed Unity {job.Request.EditorVersion}.",
                        percentage: 100);
                    continue;
                }

                lock (_sync)
                {
                    foreach (var module in job.Modules)
                    {
                        module.Phase = "Waiting";
                        module.Message = "Waiting for Unity CLI to schedule this module.";
                    }
                }

                var repairing = job.Request.OperationKind == UnityModuleOperationKind.Repair;
                UpdateJob(
                    job,
                    UnityModuleInstallationState.InstallingModules,
                    repairing ? "Resolving repair" : "Resolving modules",
                    repairing
                        ? $"Unity CLI is preparing {job.Request.Modules[0].Name} for repair."
                        : $"Unity CLI is scheduling {job.Request.ModuleCount} selected module{(job.Request.ModuleCount == 1 ? string.Empty : "s")}.",
                    percentage: null);

                var moduleProgress = new Progress<UnityModuleInstallProgress>(
                    progress => UpdateModuleProgress(job, progress));

                var result = repairing
                    ? await _moduleService.RepairAsync(
                        job.Request.EditorVersion,
                        job.Request.EditorInstallDirectory,
                        job.Request.ModuleIds,
                        moduleProgress,
                        line => AppendLogLine(job, line),
                        cancellationToken)
                    : await _moduleService.InstallAsync(
                        job.Request.EditorVersion,
                        job.Request.EditorInstallDirectory,
                        job.Request.ModuleIds,
                        moduleProgress,
                        line => AppendLogLine(job, line),
                        cancellationToken);

                AppendLogLine(job, $"Result: {result.Message}");
                if (!result.Succeeded)
                {
                    lock (_sync)
                    {
                        job.ErrorDetails = result.Message;
                        foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                        {
                            module.HasError = true;
                            module.Phase = "Failed";
                            module.Message = result.Message;
                        }
                    }

                    UpdateJob(
                        job,
                        UnityModuleInstallationState.Failed,
                            repairing ? "Repair failed" : "Installation failed",
                        result.Message,
                        job.Percentage);
                    continue;
                }

                lock (_sync)
                {
                    foreach (var module in job.Modules)
                    {
                        module.Phase = repairing ? "Repaired" : "Installed";
                        module.Message = repairing ? "Repaired successfully." : "Installed successfully.";
                        module.Percentage = 100;
                        module.IsCompleted = true;
                        module.HasError = false;
                    }
                }

                UpdateJob(
                    job,
                    UnityModuleInstallationState.Succeeded,
                    repairing ? "Repair complete" : "Installation complete",
                    repairing
                        ? $"Repaired {job.Request.Modules[0].Name} for Unity {job.Request.EditorVersion}."
                        : $"Installed the selected modules for Unity {job.Request.EditorVersion}.",
                    percentage: 100);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                lock (_sync)
                {
                    foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                    {
                        module.Phase = job.PauseRequested ? "Paused" : "Canceled";
                        module.Message = job.PauseRequested
                            ? "Downloaded data was preserved."
                            : job.Request.OperationKind == UnityModuleOperationKind.Remove
                                ? "Module removal was canceled. Retry to finish safely."
                                : "Download or installation was canceled.";
                    }
                }

                AppendLogLine(job, job.PauseRequested
                    ? "Result: The operation was paused; cached downloads were preserved."
                    : "Result: The operation was canceled by the user.");
                UpdateJob(
                    job,
                    job.PauseRequested
                        ? UnityModuleInstallationState.Paused
                        : UnityModuleInstallationState.Canceled,
                    job.PauseRequested ? "Paused" : "Installation canceled",
                    job.PauseRequested
                        ? $"Paused Unity {job.Request.EditorVersion}. Resume to continue from downloaded data."
                        : job.Request.OperationKind switch
                        {
                            UnityModuleOperationKind.Repair => $"Stopped module repair for Unity {job.Request.EditorVersion}.",
                            UnityModuleOperationKind.Remove => $"Stopped module removal for Unity {job.Request.EditorVersion}. Retry to finish safely.",
                            _ => $"Stopped the module installation for Unity {job.Request.EditorVersion}."
                        },
                    percentage: job.PauseRequested ? job.Percentage : null);
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    job.ErrorDetails = ex.ToString();
                    foreach (var module in job.Modules.Where(module => !module.IsCompleted))
                    {
                        module.HasError = true;
                        module.Phase = "Failed";
                        module.Message = ex.Message;
                    }
                }

                AppendLogLine(job, ex.ToString());
                UpdateJob(
                    job,
                    UnityModuleInstallationState.Failed,
                    "Installation failed",
                    ex.Message,
                    job.Percentage);
            }
            finally
            {
                job.Cancellation.Dispose();
                lock (_sync)
                {
                    AddRecentLocked(job);
                    if (ReferenceEquals(_current, job))
                    {
                        _current = null;
                    }
                }
                PersistRecoverableOperations();
            }
        }
    }

    private void UpdateModuleProgress(
        InstallationJob job,
        UnityModuleInstallProgress progress)
    {
        UnityModuleInstallationSnapshot snapshot;
        lock (_sync)
        {
            // Progress<T> dispatches callbacks asynchronously. A callback queued before an
            // operation completes can therefore arrive after its terminal snapshot. Terminal,
            // paused, and transition states are immutable so stale progress cannot resurrect a
            // completed removal as an active "Removing" operation.
            if (!CanAcceptProgress(job.State))
            {
                return;
            }

            var module = FindModule(job, progress);
            var phase = FormatPhase(progress.Phase);
            var message = BuildProgressMessage(progress, phase);
            if (module is not null)
            {
                module.Phase = phase;
                module.Message = message;
                // Unity CLI's pct value is for the whole command, not the
                // individual module named in msg. Keep the active module
                // indeterminate and expose pct only on the operation summary.
                module.Percentage = null;
                module.BytesReceived = progress.BytesReceived;
                module.TotalBytes = progress.TotalBytes;
                module.IsCompleted = phase.Equals("Installed", StringComparison.OrdinalIgnoreCase)
                    || phase.Equals("Completed", StringComparison.OrdinalIgnoreCase);
                module.HasError = false;
            }

            job.State = job.Request.InstallsEditor
                ? UnityModuleInstallationState.InstallingEditor
                : UnityModuleInstallationState.InstallingModules;
            job.Phase = phase;
            job.Message = module is null
                ? message
                : $"{module.Name}: {message}";
            UpdateAggregateProgressLocked(job, progress);
            snapshot = job.CreateSnapshot();
        }

        PublishSnapshot(snapshot);
    }

    private static bool CanAcceptProgress(UnityModuleInstallationState state)
        => state is UnityModuleInstallationState.Queued
            or UnityModuleInstallationState.Preparing
            or UnityModuleInstallationState.DownloadingCli
            or UnityModuleInstallationState.InstallingEditor
            or UnityModuleInstallationState.InstallingModules;

    private static ModuleJob? FindModule(
        InstallationJob job,
        UnityModuleInstallProgress progress)
    {
        var identifier = FirstNonEmpty(
            progress.ModuleId,
            progress.ModuleName,
            ExtractProgressItemName(progress.Message));
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            var existing = job.Modules.FirstOrDefault(module =>
                module.Id.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                || module.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase));
            existing ??= job.Modules.FirstOrDefault(module =>
                ModuleIdentifiersMatch(module.Id, identifier)
                || ModuleIdentifiersMatch(module.Name, identifier));
            if (existing is not null)
            {
                return existing;
            }

            var dependency = new ModuleJob
            {
                Id = identifier,
                Name = string.IsNullOrWhiteSpace(progress.ModuleName)
                    ? identifier
                    : progress.ModuleName,
                DownloadSizeBytes = 0,
                IsDependency = true,
                Phase = "Resolving dependencies",
                Message = "Added automatically by Unity CLI."
            };
            job.Modules.Add(dependency);
            return dependency;
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            return job.Modules.FirstOrDefault(module =>
                progress.Message.Contains(module.Id, StringComparison.OrdinalIgnoreCase)
                || progress.Message.Contains(module.Name, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static string? ExtractProgressItemName(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var trimmed = message.Trim();
        foreach (var prefix in new[] { "Downloading ", "Installing " })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim().TrimEnd('.', '…');
            }
        }

        return null;
    }

    private static bool ModuleIdentifiersMatch(string candidate, string reported)
        => CanonicalizeModuleIdentifier(candidate).Equals(
            CanonicalizeModuleIdentifier(reported),
            StringComparison.Ordinal);

    private static string CanonicalizeModuleIdentifier(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

        if (normalized.StartsWith("unityeditor", StringComparison.Ordinal)
            || (normalized.StartsWith("unity", StringComparison.Ordinal)
                && value.Contains('(')))
        {
            return "unityeditor";
        }

        if (normalized.StartsWith("androidndk", StringComparison.Ordinal))
        {
            return "androidndk";
        }

        if (normalized.StartsWith("cmake", StringComparison.Ordinal))
        {
            return "cmake";
        }

        if (normalized.StartsWith("androidsdkbuildtools", StringComparison.Ordinal))
        {
            return "androidsdkbuildtools";
        }

        if (normalized.StartsWith("androidsdkplatformtools", StringComparison.Ordinal))
        {
            return "androidsdkplatformtools";
        }

        if (normalized.StartsWith("androidsdkcommandlinetools", StringComparison.Ordinal))
        {
            return "androidsdkcommandlinetools";
        }

        const string platformsPrefix = "androidsdkplatforms";
        if (normalized.StartsWith(platformsPrefix, StringComparison.Ordinal))
        {
            var version = normalized[platformsPrefix.Length..];
            var majorLength = version.TakeWhile(char.IsDigit).Count();
            var major = majorLength > 0 ? version[..Math.Min(2, majorLength)] : string.Empty;
            return platformsPrefix + major;
        }

        return normalized;
    }

    private static string BuildProgressMessage(
        UnityModuleInstallProgress progress,
        string phase)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            return progress.Message.Trim();
        }

        if (progress.BytesReceived is >= 0 && progress.TotalBytes is > 0)
        {
            return $"{FormatBytes(progress.BytesReceived.Value)} of {FormatBytes(progress.TotalBytes.Value)}";
        }

        if (progress.Percentage is not null)
        {
            return $"{Math.Clamp(progress.Percentage.Value, 0, 100):0}% complete";
        }

        return phase;
    }

    private static void UpdateAggregateProgressLocked(
        InstallationJob job,
        UnityModuleInstallProgress fallbackProgress)
    {
        if (fallbackProgress.Percentage is not null)
        {
            var reportedPercentage = ClampPercentage(fallbackProgress.Percentage);
            // Unity CLI percentages are phase-local and can legitimately reset
            // when downloading transitions to installation. Reflect the current
            // phase instead of freezing the UI at a previous 100% value.
            job.Percentage = reportedPercentage;
        }
        else
        {
            job.Percentage = null;
        }

        var receivedValues = job.Modules
            .Where(module => module.BytesReceived is not null)
            .Select(module => module.BytesReceived!.Value)
            .ToArray();
        var totalValues = job.Modules
            .Where(module => module.TotalBytes is not null)
            .Select(module => module.TotalBytes!.Value)
            .ToArray();
        job.BytesReceived = receivedValues.Length > 0 ? receivedValues.Sum() : fallbackProgress.BytesReceived;
        job.TotalBytes = totalValues.Length > 0 ? totalValues.Sum() : fallbackProgress.TotalBytes;
    }

    private void UpdateJob(
        InstallationJob job,
        UnityModuleInstallationState state,
        string phase,
        string message,
        double? percentage,
        long? bytesReceived = null,
        long? totalBytes = null)
    {
        UnityModuleInstallationSnapshot snapshot;
        lock (_sync)
        {
            if ((job.State == UnityModuleInstallationState.Canceling
                    && state is not UnityModuleInstallationState.Canceled)
                || (job.State == UnityModuleInstallationState.Pausing
                    && state is not UnityModuleInstallationState.Paused))
            {
                return;
            }

            job.State = state;
            job.Phase = phase;
            job.Message = message;
            job.Percentage = ClampPercentage(percentage);
            job.BytesReceived = bytesReceived;
            job.TotalBytes = totalBytes;
            snapshot = job.CreateSnapshot();
        }

        PublishSnapshot(snapshot);
    }

    private static async Task<string?> CreateLogAsync(InstallationJob job)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FluenityHub",
                "Logs",
                "ModuleInstallations");
            Directory.CreateDirectory(root);

            var safeVersion = string.Concat(
                job.Request.EditorVersion.Select(character =>
                    Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            var path = Path.Combine(
                root,
                $"{DateTime.Now:yyyyMMdd-HHmmss}-Unity-{safeVersion}-{job.Id:N}.log");
            var text = new StringBuilder()
                .AppendLine("FluenityHub Unity module installation log")
                .AppendLine($"Started: {DateTimeOffset.Now:O}")
                .AppendLine($"Unity editor: {job.Request.EditorVersion}")
                .AppendLine($"Editor directory: {job.Request.EditorInstallDirectory}")
                .AppendLine($"Selected modules: {string.Join(", ", job.Request.ModuleIds)}")
                .AppendLine()
                .AppendLine("Unity CLI output")
                .AppendLine("----------------")
                .ToString();
            await File.WriteAllTextAsync(path, text, Encoding.UTF8, CancellationToken.None);
            return path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to save module installation log: {ex}");
            return null;
        }
    }

    private static void AppendLogLine(InstallationJob job, string line)
    {
        if (string.IsNullOrWhiteSpace(job.LogFilePath)
            || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            lock (job.LogSync)
            {
                File.AppendAllText(
                    job.LogFilePath,
                    $"[{DateTimeOffset.Now:O}] {Helpers.SensitiveDataRedactor.Redact(line)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to append module installation log: {ex}");
        }
    }

    private void Publish(InstallationJob? changedJob)
    {
        UnityModuleInstallationSnapshot? snapshot;
        lock (_sync)
        {
            snapshot = changedJob?.CreateSnapshot();
        }

        PublishSnapshot(snapshot);
    }

    private void PublishSnapshot(UnityModuleInstallationSnapshot? changedOperation)
    {
        UnityModuleInstallationChangedEventArgs args;
        lock (_sync)
        {
            args = new(
                changedOperation,
                _current?.CreateSnapshot(),
                _queue.Count);
        }

        void RaiseChanged()
            => OperationChanged?.Invoke(this, args);

        var dispatcher = _dispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            RaiseChanged();
        }
        else
        {
            dispatcher.TryEnqueue(RaiseChanged);
        }
    }

    private void AddRecentLocked(InstallationJob job)
    {
        _recent.RemoveAll(item => item.Id == job.Id);
        _recent.Insert(0, job);
        if (_recent.Count > RetainedOperationCount)
        {
            _recent.RemoveRange(RetainedOperationCount, _recent.Count - RetainedOperationCount);
        }
    }

    private void LoadPersistedOperations()
    {
        try
        {
            if (!File.Exists(PersistencePath))
            {
                return;
            }

            var persisted = JsonSerializer.Deserialize(
                File.ReadAllText(PersistencePath),
                AppJsonContext.Default.ListPersistedUnityInstallation) ?? [];
            foreach (var item in persisted)
            {
                var request = new UnityModuleInstallationRequest(
                    item.EditorVersion,
                    item.EditorInstallDirectory,
                    item.Modules,
                    CliRelease: null)
                {
                    InstallsEditor = item.InstallsEditor,
                    ResumeInterruptedDownload = true,
                    EditorRevision = item.EditorRevision,
                    OperationKind = item.OperationKind
                };
                _recent.Add(new InstallationJob
                {
                    Id = item.Id,
                    Request = request,
                    Modules = item.Modules.Select(module => new ModuleJob
                    {
                        Id = module.Id,
                        Name = module.Name,
                        DownloadSizeBytes = module.DownloadSizeBytes,
                        Phase = "Paused",
                        Message = "Resume to continue from downloaded data."
                    }).ToList(),
                    State = UnityModuleInstallationState.Paused,
                    Phase = "Paused",
                    Message = "FluenityHub closed before this installation finished. Resume to continue from downloaded data."
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to restore installation queue: {ex}");
        }
    }

    private void PersistRecoverableOperations()
    {
        List<PersistedUnityInstallation> persisted;
        lock (_sync)
        {
            var jobs = new List<InstallationJob>();
            if (_current is not null && !_current.CreateSnapshot().IsTerminal)
            {
                jobs.Add(_current);
            }
            jobs.AddRange(_queue);
            jobs.AddRange(_recent.Where(item => item.State == UnityModuleInstallationState.Paused));
            persisted = jobs
                .DistinctBy(item => item.Id)
                .Select(item => new PersistedUnityInstallation(
                    item.Id,
                    item.Request.EditorVersion,
                    item.Request.EditorInstallDirectory,
                    item.Request.Modules.ToList(),
                    item.Request.InstallsEditor,
                    item.Request.EditorRevision,
                    item.Request.OperationKind))
                .ToList();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PersistencePath)!);
            if (persisted.Count == 0)
            {
                File.Delete(PersistencePath);
                return;
            }

            File.WriteAllText(
                PersistencePath,
                JsonSerializer.Serialize(
                    persisted,
                    AppJsonContext.Default.ListPersistedUnityInstallation));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to persist installation queue: {ex}");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static double? ClampPercentage(double? percentage)
        => percentage is null || !double.IsFinite(percentage.Value)
            ? null
            : Math.Clamp(percentage.Value, 0, 100);

    private static bool IsSameEditor(string? left, string right)
        => !string.IsNullOrWhiteSpace(left)
           && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInsideEditor(string fullPath, string? editorDirectory)
    {
        if (string.IsNullOrWhiteSpace(editorDirectory))
        {
            return false;
        }

        var directory = Path.GetFullPath(editorDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatPhase(string? phase)
    {
        var normalized = phase?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "download" or "downloading" => "Downloading",
            "install" or "installing" => "Installing",
            "verify" or "verifying" => "Verifying",
            "extract" or "extracting" => "Extracting",
            "resolve" or "resolving" => "Resolving dependencies",
            "complete" or "completed" or "success" or "succeeded" => "Installed",
            { Length: > 0 } => char.ToUpperInvariant(normalized[0]) + normalized[1..],
            _ => "Processing"
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }
}
