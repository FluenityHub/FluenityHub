using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluenityHub_WinUIHost.Services;

public sealed record AppUpdateInfo(
    bool HasUpdate, string CurrentVersion, string LatestVersion, string ReleaseTitle,
    string ReleaseNotes, string ReleaseUrl, string? DownloadUrl, string? ErrorMessage = null,
    string? AssetName = null, string? Sha256 = null, long DownloadSize = 0);

public static class AppUpdateService
{
    public const string CurrentVersion = "1.2.0";
    internal const string PackageName = "B2EEE620-A605-4BDE-855D-C1E1A7E311D0";
    internal const string Publisher = "CN=FluenityHub";
    internal const string SigningCertificateSha256 = "65C3E7E9D773D552587DAA7B61210AABCDA21094842D7C2B9E6C4C8EDDE66F4D";
    internal const string ReleasesUrl = "https://github.com/FluenityHub/FluenityHub/releases";
    internal const long MaximumDownloadSize = 2L * 1024 * 1024 * 1024;
    private const string ApiUrl = "https://api.github.com/repos/FluenityHub/FluenityHub/releases";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    static AppUpdateService() => Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluenityHub", CurrentVersion));

    internal static bool IsMsixInstallation
    {
        get
        {
            try
            {
                var id = Windows.ApplicationModel.Package.Current.Id;
                return id.Name == PackageName && id.Publisher == Publisher;
            }
            catch (InvalidOperationException) { return false; }
        }
    }

    public static async Task<AppUpdateInfo> CheckForUpdatesAsync()
    {
        try
        {
            NetworkConnectivityService.Current.EnsureCanAttemptInternet();
            using var response = await Client.GetAsync(ApiUrl + "/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return NoUpdate();
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return SelectRelease(document.RootElement, IsMsixInstallation,
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or OperationCanceledException or InvalidDataException or OfflineException)
        {
            return NoUpdate(NetworkConnectivityService.Current.GetUserMessage(ex, "the FluenityHub update service"));
        }
    }

    private static AppUpdateInfo NoUpdate(string? error = null) => new(false, CurrentVersion, CurrentVersion,
        string.Empty, string.Empty, ReleasesUrl, null, error);

    internal static AppUpdateInfo SelectRelease(JsonElement selected, bool msix, string architecture, string current)
    {
        if (selected.ValueKind != JsonValueKind.Object || IsTrue(selected, "draft") || IsTrue(selected, "prerelease"))
            return new(false, current, current, string.Empty, string.Empty, ReleasesUrl, null);

        var selectedVersion = GetString(selected, "tag_name").TrimStart('v', 'V');
        if (!TryParseVersion(selectedVersion, out _, out var suffix) || suffix.Length > 0)
            return new(false, current, current, string.Empty, string.Empty, ReleasesUrl, null);

        var hasUpdate = CompareVersions(selectedVersion, current) > 0;
        var title = GetString(selected, "name");
        var notes = GetString(selected, "body");
        var releaseUrl = GetString(selected, "html_url");
        if (!releaseUrl.StartsWith(ReleasesUrl + "/tag/", StringComparison.Ordinal)) releaseUrl = ReleasesUrl;
        if (selected.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var expectedName = $"FluenityHub-{selectedVersion}-win-{architecture}{(msix ? ".msix" : "-portable.zip")}";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || GetString(asset, "name") != expectedName) continue;
                var url = GetString(asset, "browser_download_url");
                var digest = GetString(asset, "digest");
                if (!IsTrustedAssetUrl(url) || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                    || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit)
                    || !asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size)
                    || size <= 0 || size > MaximumDownloadSize) continue;
                return new(hasUpdate, current, selectedVersion, title, notes, releaseUrl, url,
                    AssetName: expectedName, Sha256: digest[7..], DownloadSize: size);
            }
        }
        return new(hasUpdate, current, selectedVersion, title, notes, releaseUrl, null,
            hasUpdate ? "This release has no verified download for your installation. Open its release notes to download it manually." : null);
    }

    internal static bool IsTrustedAssetUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.Host == "github.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith("/FluenityHub/FluenityHub/releases/download/", StringComparison.Ordinal)
            && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static string GetString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;
    private static bool IsTrue(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    internal static int CompareVersions(string left, string right)
    {
        if (!TryParseVersion(left, out var leftCore, out var leftPre)
            || !TryParseVersion(right, out var rightCore, out var rightPre))
            throw new InvalidDataException("The release version is not valid.");
        var result = leftCore.CompareTo(rightCore);
        if (result != 0) return result;
        if (leftPre.Length == 0 || rightPre.Length == 0) return (leftPre.Length == 0 ? 1 : 0).CompareTo(rightPre.Length == 0 ? 1 : 0);
        for (var index = 0; index < Math.Min(leftPre.Length, rightPre.Length); index++)
        {
            var a = leftPre[index];
            var b = rightPre[index];
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            result = aNumeric && bNumeric ? a.Length.CompareTo(b.Length) : aNumeric != bNumeric ? (aNumeric ? -1 : 1) : 0;
            if (result == 0) result = string.CompareOrdinal(a, b);
            if (result != 0) return result;
        }
        return leftPre.Length.CompareTo(rightPre.Length);
    }

    private static bool TryParseVersion(string value, out Version core, out string[] prerelease)
    {
        core = new Version(0, 0, 0);
        prerelease = [];
        if (value.Length > 128) return false;
        var match = Regex.Match(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || !Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var parsed)) return false;
        prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit))) return false;
        core = parsed;
        return true;
    }
}
