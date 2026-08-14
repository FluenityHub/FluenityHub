using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Contains metadata about a detected FluenityHub release update.
/// </summary>
public sealed record AppUpdateInfo(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string ReleaseUrl,
    string? DownloadUrl
);

/// <summary>
/// Asynchronously checks GitHub Releases for new FluenityHub updates.
/// </summary>
public static class AppUpdateService
{
    public const string CurrentVersion = "1.0.0";
    
    private const string GitHubRepoOwner = "FluenityHub";
    private const string GitHubRepoName = "FluenityHub";
    private const string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubRepoOwner}/{GitHubRepoName}/releases/latest";

    private static readonly HttpClient HttpClient = new();

    static AppUpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("FluenityHub", CurrentVersion));
        HttpClient.Timeout = TimeSpan.FromSeconds(8);
    }

    public static async Task<AppUpdateInfo> CheckForUpdatesAsync()
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            var response = await HttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return new AppUpdateInfo(
                    HasUpdate: false,
                    CurrentVersion: CurrentVersion,
                    LatestVersion: CurrentVersion,
                    ReleaseTitle: string.Empty,
                    ReleaseNotes: string.Empty,
                    ReleaseUrl: $"https://github.com/{GitHubRepoOwner}/{GitHubRepoName}/releases",
                    DownloadUrl: null
                );
            }

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp)
                ? tagProp.GetString()?.TrimStart('v', 'V') ?? CurrentVersion
                : CurrentVersion;

            string name = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? $"v{tagName}"
                : $"v{tagName}";

            string body = root.TryGetProperty("body", out var bodyProp)
                ? bodyProp.GetString() ?? string.Empty
                : string.Empty;

            string htmlUrl = root.TryGetProperty("html_url", out var urlProp)
                ? urlProp.GetString() ?? $"https://github.com/{GitHubRepoOwner}/{GitHubRepoName}/releases"
                : $"https://github.com/{GitHubRepoOwner}/{GitHubRepoName}/releases";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var dlProp))
                    {
                        var url = dlProp.GetString();
                        if (url != null && (url.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                                            url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                            url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                        {
                            downloadUrl = url;
                            break;
                        }
                    }
                }
            }

            bool hasUpdate = IsVersionGreater(tagName, CurrentVersion);

            return new AppUpdateInfo(
                HasUpdate: hasUpdate,
                CurrentVersion: CurrentVersion,
                LatestVersion: tagName,
                ReleaseTitle: name,
                ReleaseNotes: body,
                ReleaseUrl: htmlUrl,
                DownloadUrl: downloadUrl
            );
        }
        catch
        {
            return new AppUpdateInfo(
                HasUpdate: false,
                CurrentVersion: CurrentVersion,
                LatestVersion: CurrentVersion,
                ReleaseTitle: string.Empty,
                ReleaseNotes: string.Empty,
                ReleaseUrl: $"https://github.com/{GitHubRepoOwner}/{GitHubRepoName}/releases",
                DownloadUrl: null
            );
        }
    }

    private static bool IsVersionGreater(string latestVersionStr, string currentVersionStr)
    {
        if (Version.TryParse(latestVersionStr, out var latest) &&
            Version.TryParse(currentVersionStr, out var current))
        {
            return latest > current;
        }

        return string.Compare(latestVersionStr, currentVersionStr, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
