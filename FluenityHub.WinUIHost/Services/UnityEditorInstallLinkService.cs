using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityEditorInstallLinkService
{
    private static readonly Regex LinkPattern = new(
        @"\Aunityhub://(?<version>[0-9]{4}\.[0-9]+\.[0-9]+[abfp][0-9]+(?:c[0-9]+)?)/(?<revision>[a-fA-F0-9]{12,40})/?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static (string Version, string Revision) Parse(string link)
    {
        if (link.Length > 512) throw new ArgumentException("Enter a Unity Editor install link.");
        var match = LinkPattern.Match(link.Trim());
        if (!match.Success) throw new ArgumentException("Use a unityhub://version/changeset link from Unity. Web download URLs and sign-in links are not accepted.");
        return (match.Groups["version"].Value, match.Groups["revision"].Value.ToLowerInvariant());
    }

    public static async Task<UnityEditorRelease> ResolveAsync(string link, CancellationToken cancellationToken)
    {
        var (version, revision) = Parse(link);
        NetworkConnectivityService.Current.EnsureCanAttemptInternet();
        var result = await UnityCliCommandService.RunAsync(
            ["install", version, "--changeset", revision, "--architecture", "x86_64", "--dry-run", "--yes"], cancellationToken);
        if (!result.Succeeded) throw new IOException(result.Message);
        if (result.Data.ValueKind != JsonValueKind.Object
            || !result.Data.TryGetProperty("editor", out var editor) || editor.ValueKind != JsonValueKind.Object
            || !editor.TryGetProperty("version", out var resolvedVersion) || resolvedVersion.GetString() != version)
            throw new InvalidDataException("Unity returned a different Editor version for this link.");
        if (result.Data.TryGetProperty("alreadyInstalled", out var installed) && installed.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"Unity {version} is already installed.");
        var bytes = editor.TryGetProperty("downloadSize", out var size) && size.TryGetInt64(out var value) ? Math.Max(0, value) : 0;
        return new(version, DateTimeOffset.MinValue, "CUSTOM", false, revision, null, bytes, 0, []);
    }
}
