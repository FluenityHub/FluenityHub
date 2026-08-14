using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;

namespace FluenityHub_WinUIHost.Models;

public sealed class UnityModuleProgressListItem : INotifyPropertyChanged
{
    private UnityModuleProgressSnapshot _snapshot;

    public UnityModuleProgressListItem(UnityModuleProgressSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _snapshot.Id;
    public string Name => _snapshot.Name;
    public string Phase => _snapshot.Phase;
    public string DisplayPhase => IsWaiting ? "Waiting" : Phase;
    public string Message => _snapshot.Message;
    public string Details => _snapshot.IsDependency
        ? $"Dependency · {Message}"
        : Message;
    public string StatusGlyph
    {
        get
        {
            if (_snapshot.HasError)
            {
                return "\uEA39";
            }

            if (_snapshot.IsCompleted)
            {
                return "\uE73E";
            }

            if (_snapshot.Phase.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
                || _snapshot.Phase.Equals("Canceling", StringComparison.OrdinalIgnoreCase)
                || _snapshot.Phase.Equals("Paused", StringComparison.OrdinalIgnoreCase)
                || _snapshot.Phase.Equals("Pausing", StringComparison.OrdinalIgnoreCase))
            {
                return "\uE711";
            }

            if (IsWaiting)
            {
                return "\uE823";
            }

            return "\uE896";
        }
    }
    public string StatusAutomationName => $"{Name}: {DisplayPhase}. {Message}";
    public double Percentage => _snapshot.Percentage ?? 0;
    public string PercentageText =>
        _snapshot.Percentage is double percentage ? $"{percentage:0}%" : string.Empty;
    public Visibility PercentageVisibility =>
        _snapshot.Percentage is null ? Visibility.Collapsed : Visibility.Visible;
    public bool IsIndeterminate =>
        !_snapshot.IsCompleted
        && !_snapshot.HasError
        && _snapshot.Percentage is null
        && !IsWaiting
        && !_snapshot.Phase.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
        && !_snapshot.Phase.Equals("Paused", StringComparison.OrdinalIgnoreCase)
        && !_snapshot.Phase.Equals("Pausing", StringComparison.OrdinalIgnoreCase);
    public bool ShowError => _snapshot.HasError;
    public bool ShowPaused =>
        _snapshot.Phase.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
        || _snapshot.Phase.Equals("Paused", StringComparison.OrdinalIgnoreCase)
        || _snapshot.Phase.Equals("Pausing", StringComparison.OrdinalIgnoreCase);
    public string ProgressAutomationName => $"{Name} module installation progress";

    private bool IsWaiting =>
        _snapshot.Phase.Equals("Queued", StringComparison.OrdinalIgnoreCase)
        || _snapshot.Phase.Equals("Waiting", StringComparison.OrdinalIgnoreCase)
        || (_snapshot.Phase.Equals("Preparing", StringComparison.OrdinalIgnoreCase)
            && _snapshot.Message.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase));

    public void Update(UnityModuleProgressSnapshot snapshot)
    {
        if (!snapshot.Id.Equals(Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The snapshot does not belong to this module.", nameof(snapshot));
        }

        var previousPhase = Phase;
        var previousDisplayPhase = DisplayPhase;
        var previousMessage = Message;
        var previousDetails = Details;
        var previousStatusGlyph = StatusGlyph;
        var previousStatusAutomationName = StatusAutomationName;
        var previousPercentage = Percentage;
        var previousPercentageText = PercentageText;
        var previousPercentageVisibility = PercentageVisibility;
        var previousIsIndeterminate = IsIndeterminate;
        var previousShowError = ShowError;
        var previousShowPaused = ShowPaused;

        _snapshot = snapshot;

        NotifyIfChanged(previousPhase, Phase, nameof(Phase));
        NotifyIfChanged(previousDisplayPhase, DisplayPhase, nameof(DisplayPhase));
        NotifyIfChanged(previousMessage, Message, nameof(Message));
        NotifyIfChanged(previousDetails, Details, nameof(Details));
        NotifyIfChanged(previousStatusGlyph, StatusGlyph, nameof(StatusGlyph));
        NotifyIfChanged(
            previousStatusAutomationName,
            StatusAutomationName,
            nameof(StatusAutomationName));
        NotifyIfChanged(previousPercentage, Percentage, nameof(Percentage));
        NotifyIfChanged(previousPercentageText, PercentageText, nameof(PercentageText));
        NotifyIfChanged(
            previousPercentageVisibility,
            PercentageVisibility,
            nameof(PercentageVisibility));
        NotifyIfChanged(previousIsIndeterminate, IsIndeterminate, nameof(IsIndeterminate));
        NotifyIfChanged(previousShowError, ShowError, nameof(ShowError));
        NotifyIfChanged(previousShowPaused, ShowPaused, nameof(ShowPaused));
    }

    private void NotifyIfChanged<T>(T previousValue, T currentValue, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previousValue, currentValue))
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class UnityModuleInstallationListItem : INotifyPropertyChanged
{
    private UnityModuleInstallationSnapshot _snapshot;

    public UnityModuleInstallationListItem(
        UnityModuleInstallationSnapshot snapshot,
        string? iconPath)
    {
        _snapshot = snapshot;
        IconPath = string.IsNullOrWhiteSpace(iconPath)
            ? "ms-appx:///Assets/FluenityHub_Logo.png"
            : iconPath;
        Modules = new ObservableCollection<UnityModuleProgressListItem>(
            snapshot.Modules.Select(module => new UnityModuleProgressListItem(module)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UnityModuleProgressListItem> Modules { get; }
    public Guid Id => _snapshot.Id;
    public string IconPath { get; }
    public string EditorVersion => $"Unity {_snapshot.EditorVersion}";
    public string Phase => _snapshot.Phase;
    public string Message => _snapshot.Message;
    public string Details => _snapshot.OperationKind switch
    {
        UnityModuleOperationKind.Repair => $"Repairing {_snapshot.Modules[0].Name}",
        UnityModuleOperationKind.Remove => $"Removing {_snapshot.Modules[0].Name}",
        _ => $"{_snapshot.ModuleCount} selected module{(_snapshot.ModuleCount == 1 ? string.Empty : "s")} · "
            + $"{FormatBytes(_snapshot.DownloadSizeBytes)} download"
    };
    public double Percentage => _snapshot.Percentage ?? 0;
    public string PercentageText =>
        _snapshot.Percentage is double percentage ? $"{percentage:0}%" : string.Empty;
    public Visibility PercentageVisibility =>
        _snapshot.Percentage is null ? Visibility.Collapsed : Visibility.Visible;
    public bool IsIndeterminate =>
        _snapshot.State is not UnityModuleInstallationState.Queued
            and not UnityModuleInstallationState.Canceling
            and not UnityModuleInstallationState.Pausing
            and not UnityModuleInstallationState.Paused
            and not UnityModuleInstallationState.Succeeded
            and not UnityModuleInstallationState.Failed
            and not UnityModuleInstallationState.Canceled
        && _snapshot.Percentage is null;
    public bool IsCanceling => _snapshot.State == UnityModuleInstallationState.Canceling;
    public bool IsPausing => _snapshot.State == UnityModuleInstallationState.Pausing;
    public bool IsPaused => _snapshot.State == UnityModuleInstallationState.Paused;
    public bool ShowError => _snapshot.State == UnityModuleInstallationState.Failed;
    public bool ShowPaused => _snapshot.State == UnityModuleInstallationState.Canceled
        || IsCanceling
        || IsPausing
        || IsPaused;
    public bool CanPause => _snapshot.CanPause;
    public string PauseText => IsPausing ? "Pausing…" : "Pause";
    public Visibility PauseActionVisibility =>
        _snapshot.CanPause || IsPausing ? Visibility.Visible : Visibility.Collapsed;
    public bool CanResume => _snapshot.CanResume;
    public Visibility ResumeActionVisibility =>
        CanResume ? Visibility.Visible : Visibility.Collapsed;
    public bool CanCancel => _snapshot.CanCancel;
    public string CancelText => IsCanceling ? "Canceling…" : "Cancel";
    public Visibility CancelVisibility =>
        _snapshot.CanCancel ? Visibility.Visible : Visibility.Collapsed;
    public bool CanRetry => _snapshot.State is UnityModuleInstallationState.Failed
        or UnityModuleInstallationState.Canceled;
    public string RetryText => _snapshot.State == UnityModuleInstallationState.Canceled
        && _snapshot.OperationKind == UnityModuleOperationKind.Install
        ? "Resume"
        : "Retry";
    public Visibility RetryActionVisibility =>
        CanRetry ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OpenLogVisibility =>
        !string.IsNullOrWhiteSpace(_snapshot.LogFilePath)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility CancelSeparatorVisibility =>
        CancelVisibility == Visibility.Visible && OpenLogVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility MoreActionsVisibility =>
        OpenLogVisibility == Visibility.Visible || CancelVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility ErrorVisibility =>
        ShowError && !string.IsNullOrWhiteSpace(_snapshot.ErrorDetails)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public bool IsErrorOpen =>
        ShowError && !string.IsNullOrWhiteSpace(_snapshot.ErrorDetails);
    public string ErrorDetails => _snapshot.ErrorDetails ?? string.Empty;
    public string? LogFilePath => _snapshot.LogFilePath;
    public string ProgressAutomationName =>
        $"Module {_snapshot.OperationKind.ToString().ToLowerInvariant()} progress for {EditorVersion}";
    public string CancelAutomationName =>
        $"Cancel module {_snapshot.OperationKind.ToString().ToLowerInvariant()} for {EditorVersion}";
    public string PauseAutomationName => $"Pause module installation for {EditorVersion}";
    public string ResumeAutomationName => $"Resume module installation for {EditorVersion}";
    public string OpenLogAutomationName => $"Open download log for {EditorVersion}";
    public string OpenLogFolderAutomationName => $"Open download log folder in File Explorer for {EditorVersion}";
    public string RetryAutomationName => $"{RetryText} module installation for {EditorVersion}";

    public void Update(UnityModuleInstallationSnapshot snapshot)
    {
        if (snapshot.Id != Id)
        {
            throw new ArgumentException("The snapshot does not belong to this operation.", nameof(snapshot));
        }

        var previousPhase = Phase;
        var previousMessage = Message;
        var previousPercentage = Percentage;
        var previousPercentageText = PercentageText;
        var previousPercentageVisibility = PercentageVisibility;
        var previousIsIndeterminate = IsIndeterminate;
        var previousIsCanceling = IsCanceling;
        var previousIsPausing = IsPausing;
        var previousIsPaused = IsPaused;
        var previousShowError = ShowError;
        var previousShowPaused = ShowPaused;
        var previousCanCancel = CanCancel;
        var previousCanPause = CanPause;
        var previousPauseText = PauseText;
        var previousPauseActionVisibility = PauseActionVisibility;
        var previousCanResume = CanResume;
        var previousResumeActionVisibility = ResumeActionVisibility;
        var previousCancelText = CancelText;
        var previousCancelVisibility = CancelVisibility;
        var previousCanRetry = CanRetry;
        var previousRetryText = RetryText;
        var previousRetryActionVisibility = RetryActionVisibility;
        var previousOpenLogVisibility = OpenLogVisibility;
        var previousCancelSeparatorVisibility = CancelSeparatorVisibility;
        var previousMoreActionsVisibility = MoreActionsVisibility;
        var previousErrorVisibility = ErrorVisibility;
        var previousIsErrorOpen = IsErrorOpen;
        var previousErrorDetails = ErrorDetails;
        var previousLogFilePath = LogFilePath;

        _snapshot = snapshot;
        SyncModules(snapshot.Modules);

        NotifyIfChanged(previousPhase, Phase, nameof(Phase));
        NotifyIfChanged(previousMessage, Message, nameof(Message));
        NotifyIfChanged(previousPercentage, Percentage, nameof(Percentage));
        NotifyIfChanged(previousPercentageText, PercentageText, nameof(PercentageText));
        NotifyIfChanged(
            previousPercentageVisibility,
            PercentageVisibility,
            nameof(PercentageVisibility));
        NotifyIfChanged(previousIsIndeterminate, IsIndeterminate, nameof(IsIndeterminate));
        NotifyIfChanged(previousIsCanceling, IsCanceling, nameof(IsCanceling));
        NotifyIfChanged(previousIsPausing, IsPausing, nameof(IsPausing));
        NotifyIfChanged(previousIsPaused, IsPaused, nameof(IsPaused));
        NotifyIfChanged(previousShowError, ShowError, nameof(ShowError));
        NotifyIfChanged(previousShowPaused, ShowPaused, nameof(ShowPaused));
        NotifyIfChanged(previousCanCancel, CanCancel, nameof(CanCancel));
        NotifyIfChanged(previousCanPause, CanPause, nameof(CanPause));
        NotifyIfChanged(previousPauseText, PauseText, nameof(PauseText));
        NotifyIfChanged(
            previousPauseActionVisibility,
            PauseActionVisibility,
            nameof(PauseActionVisibility));
        NotifyIfChanged(previousCanResume, CanResume, nameof(CanResume));
        NotifyIfChanged(
            previousResumeActionVisibility,
            ResumeActionVisibility,
            nameof(ResumeActionVisibility));
        NotifyIfChanged(previousCancelText, CancelText, nameof(CancelText));
        NotifyIfChanged(previousCancelVisibility, CancelVisibility, nameof(CancelVisibility));
        NotifyIfChanged(previousCanRetry, CanRetry, nameof(CanRetry));
        NotifyIfChanged(previousRetryText, RetryText, nameof(RetryText));
        NotifyIfChanged(
            previousRetryActionVisibility,
            RetryActionVisibility,
            nameof(RetryActionVisibility));
        NotifyIfChanged(previousOpenLogVisibility, OpenLogVisibility, nameof(OpenLogVisibility));
        NotifyIfChanged(
            previousCancelSeparatorVisibility,
            CancelSeparatorVisibility,
            nameof(CancelSeparatorVisibility));
        NotifyIfChanged(
            previousMoreActionsVisibility,
            MoreActionsVisibility,
            nameof(MoreActionsVisibility));
        NotifyIfChanged(previousErrorVisibility, ErrorVisibility, nameof(ErrorVisibility));
        NotifyIfChanged(previousIsErrorOpen, IsErrorOpen, nameof(IsErrorOpen));
        NotifyIfChanged(previousErrorDetails, ErrorDetails, nameof(ErrorDetails));
        NotifyIfChanged(previousLogFilePath, LogFilePath, nameof(LogFilePath));
    }

    private void SyncModules(IReadOnlyList<UnityModuleProgressSnapshot> snapshots)
    {
        var activeIds = snapshots
            .Select(snapshot => snapshot.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Modules.Count - 1; index >= 0; index--)
        {
            if (!activeIds.Contains(Modules[index].Id))
            {
                Modules.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < snapshots.Count; targetIndex++)
        {
            var snapshot = snapshots[targetIndex];
            var item = Modules.FirstOrDefault(module =>
                module.Id.Equals(snapshot.Id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                Modules.Insert(
                    Math.Min(targetIndex, Modules.Count),
                    new UnityModuleProgressListItem(snapshot));
                continue;
            }

            item.Update(snapshot);
            var currentIndex = Modules.IndexOf(item);
            if (currentIndex != targetIndex)
            {
                Modules.Move(currentIndex, targetIndex);
            }
        }
    }

    private void NotifyIfChanged<T>(T previousValue, T currentValue, string propertyName)
    {
        if (!EqualityComparer<T>.Default.Equals(previousValue, currentValue))
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        var displayValue = (double)value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{displayValue:0} {units[unitIndex]}"
            : $"{displayValue:0.##} {units[unitIndex]}";
    }
}
