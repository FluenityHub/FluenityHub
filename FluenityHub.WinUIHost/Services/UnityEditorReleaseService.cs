using System.Text.Json;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record UnityEditorRelease(
    string Version,
    DateTimeOffset ReleaseDate,
    string Stream,
    bool IsRecommended,
    string? Revision,
    Uri? ReleaseNotesUri,
    long DownloadSizeBytes,
    long InstalledSizeBytes,
    IReadOnlyList<UnityEditorModuleInfo> Modules);

public sealed record UnityEditorReleasePage(
    IReadOnlyList<UnityEditorRelease> Releases,
    int Offset,
    int Limit,
    int Total);

public sealed class UnityEditorReleaseService
{
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private const string ReleaseEndpoint =
        "https://services.api.unity.com/unity/editor/release/v1/releases";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<UnityEditorRelease?> GetReleaseAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var normalizedVersion = version.Trim();
        var page = await GetReleasesAsync(
            offset: 0,
            limit: 25,
            versionQuery: normalizedVersion,
            cancellationToken: cancellationToken);
        return page.Releases.FirstOrDefault(release =>
            release.Version.Equals(normalizedVersion, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UnityEditorReleasePage> GetReleasesAsync(
        int offset,
        int limit,
        IReadOnlyCollection<string>? streams = null,
        string? versionQuery = null,
        CancellationToken cancellationToken = default)
    {
        NetworkConnectivityService.Current.EnsureCanAttemptInternet();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 25);
        var query = new List<string>
        {
            "order=RELEASE_DATE_DESC",
            $"offset={offset}",
            $"limit={limit}",
            "platform=WINDOWS",
            "architecture=X86_64"
        };
        if (streams is not null)
        {
            query.AddRange(
                streams
                    .Where(stream => !string.IsNullOrWhiteSpace(stream))
                    .Select(stream => $"stream={Uri.EscapeDataString(stream)}"));
        }

        if (!string.IsNullOrWhiteSpace(versionQuery))
        {
            query.Add($"version={Uri.EscapeDataString(versionQuery.Trim())}");
        }

        var uri = new Uri($"{ReleaseEndpoint}?{string.Join('&', query)}");

        using var response = await HttpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (!response.RequestMessage?.RequestUri?.Host.Equals(
                "services.api.unity.com",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidDataException("Unity redirected the release request to an unexpected host.");
        }

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("Unity returned more release data than expected.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("Unity returned more release data than expected.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }

        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var releases = new List<UnityEditorRelease>();
        if (root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in results.EnumerateArray())
            {
                var version = ReadString(result, "version");
                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                var windowsDownload = FindWindowsDownload(result);
                if (windowsDownload is null)
                {
                    continue;
                }

                var releaseNotesUri = TryReadUri(
                    result.TryGetProperty("releaseNotes", out var notes)
                        ? ReadString(notes, "url")
                        : null);
                releases.Add(new UnityEditorRelease(
                    version,
                    ReadDate(result, "releaseDate"),
                    ReadString(result, "stream") ?? "TECH",
                    ReadBoolean(result, "recommended"),
                    ReadString(result, "shortRevision"),
                    releaseNotesUri,
                    ReadSize(windowsDownload.Value, "downloadSize"),
                    ReadSize(windowsDownload.Value, "installedSize"),
                    ParseModules(windowsDownload.Value)));
            }
        }

        return new UnityEditorReleasePage(
            releases,
            ReadInt(root, "offset", offset),
            ReadInt(root, "limit", limit),
            ReadInt(root, "total", releases.Count));
    }

    private static IReadOnlyList<UnityEditorModuleInfo> ParseModules(JsonElement download)
    {
        if (!download.TryGetProperty("modules", out var modules)
            || modules.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<UnityEditorModuleInfo>();
        foreach (var module in modules.EnumerateArray())
        {
            ParseModule(module, string.Empty, result);
        }

        return result;
    }

    private static void ParseModule(
        JsonElement element,
        string parentId,
        ICollection<UnityEditorModuleInfo> result)
    {
        if (ReadBoolean(element, "hidden"))
        {
            return;
        }

        var id = ReadString(element, "id");
        var name = ReadString(element, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        result.Add(new UnityEditorModuleInfo
        {
            Id = id,
            Name = name,
            Category = NormalizeCategory(ReadString(element, "category")),
            ParentId = parentId,
            DownloadSizeBytes = ReadSize(element, "downloadSize"),
            InstalledSizeBytes = ReadSize(element, "installedSize"),
            IsRequired = ReadBoolean(element, "required"),
            IsPreselected = ReadBoolean(element, "preSelected"),
            LicenseTerms = ReadLicenseTerms(element, id, name)
        });

        if (!element.TryGetProperty("subModules", out var children)
            || children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            ParseModule(child, id, result);
        }
    }

    private static IReadOnlyList<UnityLicenseTerm> ReadLicenseTerms(
        JsonElement element,
        string moduleId,
        string moduleName)
    {
        if (!element.TryGetProperty("eula", out var terms)
            || terms.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return terms
            .EnumerateArray()
            .Select(term => new UnityLicenseTerm
            {
                ModuleId = moduleId,
                ModuleName = moduleName,
                Label = ReadString(term, "label") ?? $"{moduleName} license terms",
                Message = ReadString(term, "message") ?? string.Empty,
                NavigateUri = TryReadUri(ReadString(term, "url"))
            })
            .ToArray();
    }

    private static string NormalizeCategory(string? category)
        => category?.ToUpperInvariant() switch
        {
            "DEV_TOOL" or "PLUGIN" => "Dev tools",
            "PLATFORM" => "Platforms",
            "LANGUAGE_PACK" => "Language packs (preview)",
            "DOCUMENTATION" => "Documentation",
            _ => "Other"
        };

    private static JsonElement? FindWindowsDownload(JsonElement release)
    {
        if (!release.TryGetProperty("downloads", out var downloads)
            || downloads.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var download in downloads.EnumerateArray())
        {
            if (string.Equals(ReadString(download, "platform"), "WINDOWS", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(download, "architecture"), "X86_64", StringComparison.OrdinalIgnoreCase))
            {
                return download;
            }
        }

        return null;
    }

    private static long ReadSize(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var size)
            || size.ValueKind != JsonValueKind.Object
            || !size.TryGetProperty("value", out var value))
        {
            return 0;
        }

        return value.TryGetInt64(out var result) ? Math.Max(0, result) : 0;
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();

    private static int ReadInt(JsonElement element, string propertyName, int fallback)
        => element.TryGetProperty(propertyName, out var value)
           && value.TryGetInt32(out var result)
            ? result
            : fallback;

    private static DateTimeOffset ReadDate(JsonElement element, string propertyName)
        => DateTimeOffset.TryParse(ReadString(element, propertyName), out var value)
            ? value
            : DateTimeOffset.MinValue;

    private static Uri? TryReadUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

}
