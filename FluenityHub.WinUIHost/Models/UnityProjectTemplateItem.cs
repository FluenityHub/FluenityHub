using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using FluenityHub_WinUIHost.Services;

namespace FluenityHub_WinUIHost.Models;

internal sealed class UnityProjectTemplateItem(UnityProjectTemplate template, string downloadDirectory) : INotifyPropertyChanged
{
    public UnityProjectTemplate Template { get; } = template;
    internal string DownloadDirectory { get; } = downloadDirectory;
    public bool IsDownloaded { get; private set; }
    public bool IsDownloading { get; private set; }
    public int DownloadPercent { get; private set; }
    public string ErrorMessage { get; private set; } = string.Empty;
    public bool HasDownload => Template.DownloadUri is not null;
    public bool CanDownload => !IsDownloaded;
    public bool CanUseDownloadCommand => HasDownload && CanDownload;
    public bool CanRemoveDownload => HasDownload && Template.ArchivePath is null && IsDownloaded && !IsDownloading;
    public string DownloadCommandText => IsDownloading ? "Cancel download" : "Download";
    public string DownloadCommandGlyph => IsDownloading ? "\uE711" : "\uE896";
    public string DownloadGlyph => IsDownloading ? "\uE711" : IsDownloaded ? "\uE73E" : "\uE896";
    public string DownloadAction => IsDownloading ? $"Cancel download of {Template.Name}" :
        IsDownloaded ? $"{Template.Name} is downloaded" : $"Download {Template.Name}";
    public string DownloadProgressText => $"{DownloadPercent}%";
    public bool ShowDescription => !IsDownloading;
    public ImageSource? CoverSource => Template.CustomTemplate?.CoverImageSource;
    public bool HasLocalPath => Template.CustomTemplate is not null || IsDownloaded;
    internal string? LocalPath => Template.CustomTemplate?.TemplateFolderPath ??
        (IsDownloaded ? Template.ArchivePath ??
            (HasDownload ? UnityTemplateCatalogService.GetArchiveCachePath(Template, DownloadDirectory) : null) : null);
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(bool downloaded, bool downloading = false, int percent = 0, string error = "")
    {
        var stateChanged = IsDownloaded != downloaded || IsDownloading != downloading;
        var progressChanged = DownloadPercent != percent;
        var errorChanged = ErrorMessage != error;
        IsDownloaded = downloaded;
        IsDownloading = downloading;
        DownloadPercent = percent;
        ErrorMessage = error;
        if (stateChanged)
            foreach (var property in new[] { nameof(IsDownloaded), nameof(IsDownloading), nameof(CanDownload),
                nameof(DownloadGlyph), nameof(DownloadAction), nameof(CanUseDownloadCommand), nameof(DownloadCommandText),
                nameof(DownloadCommandGlyph), nameof(HasLocalPath),
                nameof(ShowDescription), nameof(CanRemoveDownload) })
                PropertyChanged?.Invoke(this, new(property));
        if (progressChanged)
        {
            PropertyChanged?.Invoke(this, new(nameof(DownloadPercent)));
            PropertyChanged?.Invoke(this, new(nameof(DownloadProgressText)));
        }
        if (errorChanged) PropertyChanged?.Invoke(this, new(nameof(ErrorMessage)));
    }
}
