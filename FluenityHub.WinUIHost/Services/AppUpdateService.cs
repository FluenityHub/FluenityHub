using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluenityHub_WinUIHost.Services;

public sealed record AppUpdateInfo(
    bool HasUpdate, string CurrentVersion, string LatestVersion, string ReleaseTitle,
    string ReleaseNotes, string ReleaseUrl, string? ErrorMessage = null);

public static class AppUpdateService
{
    public const string CurrentVersion = "1.2.0";
    internal const string ReleasesUrl = "https://github.com/FluenityHub/FluenityHub/releases";
    private const string ApiUrl = "https://api.github.com/repos/FluenityHub/FluenityHub/releases";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    static AppUpdateService() => Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluenityHub", CurrentVersion));

    public static async Task<AppUpdateInfo> CheckForUpdatesAsync()
    {
        try
        {
            NetworkConnectivityService.Current.EnsureCanAttemptInternet();
            using var response = await Client.GetAsync(ApiUrl + "/latest");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return NoUpdate();
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return SelectRelease(document.RootElement, CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
            or JsonException or OperationCanceledException or InvalidDataException or OfflineException)
        {
            return NoUpdate(NetworkConnectivityService.Current.GetUserMessage(ex, "the FluenityHub update service"));
        }
    }

    private static AppUpdateInfo NoUpdate(string? error = null) => new(false, CurrentVersion, CurrentVersion,
        string.Empty, string.Empty, ReleasesUrl, error);

    internal static AppUpdateInfo SelectRelease(JsonElement selected, string current)
    {
        if (selected.ValueKind != JsonValueKind.Object || IsTrue(selected, "draft") || IsTrue(selected, "prerelease"))
            return new(false, current, current, string.Empty, string.Empty, ReleasesUrl);

        var tag = GetString(selected, "tag_name");
        var selectedVersion = tag.TrimStart('v', 'V');
        if (!TryParseVersion(selectedVersion, out _, out var suffix) || suffix.Length > 0)
            return new(false, current, current, string.Empty, string.Empty, ReleasesUrl);

        var hasUpdate = CompareVersions(selectedVersion, current) > 0;
        var title = GetString(selected, "name");
        var notes = GetString(selected, "body");
        var releaseUrl = $"{ReleasesUrl}/tag/{Uri.EscapeDataString(tag)}";
        return new(hasUpdate, current, selectedVersion, title, notes, releaseUrl);
    }

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
