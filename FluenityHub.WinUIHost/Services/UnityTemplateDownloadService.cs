using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityTemplateDownloadService
{
    private sealed record Download(UnityProjectTemplateItem Item, CancellationTokenSource Cancellation);
    // Pages share active transfers on the UI thread; completed transfers are not retained.
    private static readonly Dictionary<string, Download> Active = new(StringComparer.OrdinalIgnoreCase);

    internal static UnityProjectTemplateItem GetItem(UnityProjectTemplate template, string downloadDirectory)
    {
        if (template.DownloadUri is not null &&
            Active.TryGetValue(UnityTemplateCatalogService.GetArchiveCachePath(template, downloadDirectory), out var download))
            return download.Item;
        return new(template, downloadDirectory);
    }

    internal static async Task DownloadAsync(UnityProjectTemplateItem item)
    {
        var path = UnityTemplateCatalogService.GetArchiveCachePath(item.Template, item.DownloadDirectory);
        if (Active.TryGetValue(path, out var active))
        {
            active.Cancellation.Cancel();
            return;
        }
        using var cancellation = new CancellationTokenSource();
        Active.Add(path, new(item, cancellation));
        item.Update(downloaded: false, downloading: true);
        var progress = new Progress<int>(percent =>
        {
            if (!cancellation.IsCancellationRequested && item.IsDownloading &&
                Active.TryGetValue(path, out var current) && ReferenceEquals(current.Cancellation, cancellation))
                item.Update(downloaded: false, downloading: true, percent);
        });
        try
        {
            await UnityTemplateCatalogService.GetArchiveAsync(item.Template, item.DownloadDirectory, progress, cancellation.Token);
            item.Update(downloaded: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            item.Update(downloaded: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            item.Update(downloaded: false, error: NetworkConnectivityService.Current.GetUserMessage(ex, "the Unity template service"));
        }
        finally { Active.Remove(path); }
    }
}
