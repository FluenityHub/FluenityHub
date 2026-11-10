using System.Formats.Tar;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityTemplateCatalogService
{
    private const string CatalogUrl = "https://live-platform-api.prd.ld.unity3d.com/graphql";
    private const string PackagesUrl = "https://download.packages.unity.com";
    private const long MaxArchiveSize = 8L * 1024 * 1024 * 1024;
    private const int MaxCatalogSize = 8 * 1024 * 1024;
    private const int MaxPreviewSize = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ArchiveLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient CatalogClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = MaxCatalogSize
    };
    private static readonly HttpClient PackageClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private static readonly HttpClient PreviewClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 3
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = MaxPreviewSize
    };

    public static readonly UnityProjectTemplate Blank = new(
        "", "Blank project", "", "An empty project without a template.", "");

    public static async Task<(IReadOnlyList<UnityProjectTemplate> Templates, bool CatalogAvailable)> LoadAsync(
        string editorVersion, string editorExecutable, CancellationToken cancellationToken)
    {
        var localTask = Task.Run(() => ReadInstalledTemplates(editorExecutable, cancellationToken), cancellationToken);
        var line = Regex.Match(editorVersion, @"^\d+\.\d+(?=\.)").Value;
        var remoteTask = line.Length == 0
            ? Task.FromResult<List<UnityProjectTemplate>?>(null)
            : LoadCatalogAsync(line, cancellationToken);
        Task[] loading = [localTask, remoteTask];
        await Task.WhenAll(loading).ConfigureAwait(false);
        var installed = await localTask.ConfigureAwait(false);
        var remote = await remoteTask.ConfigureAwait(false);
        var installedPackages = installed.Select(item => item.PackageName).ToHashSet(StringComparer.Ordinal);
        var result = new List<UnityProjectTemplate> { Blank };
        var previews = remote?.ToDictionary(template => template.PackageName, template => template.PreviewUri, StringComparer.Ordinal);
        result.AddRange(installed.Select(template =>
            previews is not null && previews.TryGetValue(template.PackageName, out var preview)
                ? template with { PreviewUri = preview }
                : template));
        if (remote is not null)
            result.AddRange(remote.Where(item => !installedPackages.Contains(item.PackageName)));
        return (result, remote is not null);
    }

    private static async Task<List<UnityProjectTemplate>?> LoadCatalogAsync(string editorLine, CancellationToken cancellationToken)
    {
        const string query = """
            query ($versions: [String!]!) {
              getTemplates(limit: 100, skip: 0, supportedUnityEditorVersions: $versions) {
                edges { node {
                  name packageName description type termsOfService { url }
                  previewImage { url }
                  versions { name size { value } tarball { integrity url } }
                } }
              }
            }
            """;
        try
        {
            NetworkConnectivityService.Current.EnsureCanAttemptInternet();
            using var response = await CatalogClient.PostAsJsonAsync(CatalogUrl,
                new { query, variables = new { versions = new[] { editorLine } } }, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var templates = ReadCatalog(document.RootElement);
            if (templates is not null)
            {
                await SaveCatalogAsync(editorLine, document.RootElement, cancellationToken).ConfigureAwait(false);
                return templates;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or IOException or OfflineException or JsonException or OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Unity template catalog unavailable: {ex.GetType().Name}");
        }

        var ownCache = await ReadCachedCatalogAsync(GetCatalogCachePath(editorLine), cancellationToken).ConfigureAwait(false);
        if (ownCache is not null) return ownCache;

        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnityHub", "hub-gpui", "templates-cache.json");
        try
        {
            await using var cache = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (cache.Length > MaxCatalogSize) return null;
            using var document = await JsonDocument.ParseAsync(cache, cancellationToken: cancellationToken).ConfigureAwait(false);
            var entry = Field(Field(document.RootElement, "entries"), $"{editorLine}|en|{CatalogUrl}");
            return ReadCatalog(Field(entry, "payload"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string GetCatalogCachePath(string editorLine)
        => Path.Combine(new UnityHubLocationSettingsService().GetDownloadLocation(), "FluenityHubTemplates", $"catalog-{editorLine}.json");

    private static async Task SaveCatalogAsync(string editorLine, JsonElement catalog, CancellationToken cancellationToken)
    {
        var path = GetCatalogCachePath(editorLine);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException("The template cache directory is invalid."));
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
                await JsonSerializer.SerializeAsync(file, catalog, cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Template catalog cache could not be saved: {ex.GetType().Name}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    private static async Task<List<UnityProjectTemplate>?> ReadCachedCatalogAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaxCatalogSize) return null;
            using var catalog = await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ReadCatalog(catalog.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static List<UnityProjectTemplate>? ReadCatalog(JsonElement payload)
    {
        var edges = Field(Field(Field(payload, "data"), "getTemplates"), "edges");
        if (edges.ValueKind != JsonValueKind.Array) return null;
        var templates = new List<UnityProjectTemplate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges.EnumerateArray())
        {
            var item = Field(edge, "node");
            var package = Text(item, "packageName");
            var name = Text(item, "name");
            var category = Text(item, "type") switch { "CORE" => "Core", "SAMPLE" => "Sample", "LEARNING" => "Learning", _ => "" };
            var versions = Field(item, "versions");
            var preview = Text(Field(item, "previewImage"), "url");
            if (!IsTemplatePackage(package) || name.Length == 0 || category.Length == 0 ||
                Field(item, "termsOfService").ValueKind == JsonValueKind.Object ||
                versions.ValueKind != JsonValueKind.Array || !seen.Add(package)) continue;
            foreach (var release in versions.EnumerateArray())
            {
                var version = Text(release, "name");
                var size = Field(Field(release, "size"), "value");
                if (!Regex.IsMatch(version, @"^[0-9][0-9A-Za-z.+-]{0,63}\z") || size.ValueKind != JsonValueKind.Number ||
                    !size.TryGetInt64(out var length) || length <= 0 || length > MaxArchiveSize) continue;
                var tarball = Field(release, "tarball");
                var expectedUrl = $"{PackagesUrl}/{package}/-/{package}-{version}.tgz";
                var sha1 = ReadSha1(Text(tarball, "integrity"));
                if (Text(tarball, "url") != expectedUrl || sha1 is null) continue;
                templates.Add(new(package, name, category, Text(item, "description"), version,
                    DownloadUri: new Uri(expectedUrl), DownloadSize: length, ArchiveSha1: sha1,
                    PreviewUri: Uri.TryCreate(preview, UriKind.Absolute, out var previewUri) && IsPreviewUri(previewUri) ? previewUri : null));
                break;
            }
        }
        return templates;
    }

    internal static async Task<Stream?> OpenPreviewAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsPreviewUri(uri) || !NetworkConnectivityService.Current.CanAttemptInternet) return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var response = await PreviewClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentType?.MediaType is not ("image/png" or "image/jpeg" or "image/webp") ||
            response.Content.Headers.ContentLength is > MaxPreviewSize)
            return null;
        var contentLength = response.Content.Headers.ContentLength;
        var preview = new MemoryStream(contentLength is > 0 ? (int)contentLength.Value : 32 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, 32 * 1024), token).ConfigureAwait(false)) > 0)
            {
                if (preview.Length + count > MaxPreviewSize)
                {
                    preview.Dispose();
                    return null;
                }
                preview.Write(buffer, 0, count);
            }
            preview.Position = 0;
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static bool IsPreviewUri(Uri uri)
        => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.Host == "storage.googleapis.com" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath.StartsWith("/live-platform-resources-prd/templates/assets/", StringComparison.Ordinal);

    private static List<UnityProjectTemplate> ReadInstalledTemplates(string editorExecutable, CancellationToken cancellationToken)
    {
        var templates = new List<UnityProjectTemplate>();
        var editorDirectory = Path.GetDirectoryName(editorExecutable);
        if (editorDirectory is null) return templates;
        var directory = Path.Combine(editorDirectory, "Data", "Resources", "PackageManager", "ProjectTemplates");
        if (!Directory.Exists(directory)) return templates;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(directory, "com.unity.template.*.tgz"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                while (tar.GetNextEntry() is { } entry)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name != "package/package.json" || entry.Length > 128 * 1024 || entry.DataStream is not { } stream) continue;
                    using var document = JsonDocument.Parse(stream);
                    var package = Text(document.RootElement, "name");
                    var name = Text(document.RootElement, "displayName");
                    if (IsTemplatePackage(package) && name.Length > 0 && seen.Add(package))
                        templates.Add(new(package, name, "Core", Text(document.RootElement, "description"), Text(document.RootElement, "version"), path));
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Installed Unity template unreadable: {ex.GetType().Name}");
            }
        }
        return templates;
    }

    public static async Task<string> GetArchiveAsync(UnityProjectTemplate template, string downloadDirectory,
        IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (template.ArchivePath is { } local)
            return File.Exists(local) ? local : throw new FileNotFoundException("The installed template archive was not found.", local);
        var destination = GetArchiveCachePath(template, downloadDirectory);
        var gate = ArchiveLocks.GetOrAdd(destination, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await DownloadArchiveAsync(template, downloadDirectory, destination, progress, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal static string GetArchiveCachePath(UnityProjectTemplate template, string downloadDirectory)
    {
        if (!IsTemplatePackage(template.PackageName) || !Regex.IsMatch(template.Version, @"^[0-9][0-9A-Za-z.+-]{0,63}\z"))
            throw new InvalidDataException("The template package name or version is invalid.");
        return Path.GetFullPath(Path.Combine(downloadDirectory, "FluenityHubTemplates", $"{template.PackageName}-{template.Version}.tgz"));
    }

    internal static async Task<string?> FindCachedArchiveAsync(UnityProjectTemplate template, string downloadDirectory, CancellationToken cancellationToken)
    {
        if (template.ArchivePath is { } local) return File.Exists(local) ? local : null;
        var destination = GetArchiveCachePath(template, downloadDirectory);
        if (template.ArchiveSha1 is not { } expectedHash || !File.Exists(destination) || new FileInfo(destination).Length != template.DownloadSize)
            return null;
        await using var cached = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 128 * 1024, useAsync: true);
        var hash = Convert.ToHexString(await SHA1.HashDataAsync(cached, cancellationToken).ConfigureAwait(false));
        return hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ? destination : null;
    }

    internal static async Task RemoveCachedArchiveAsync(UnityProjectTemplate template, string downloadDirectory)
    {
        if (template.ArchivePath is not null || template.DownloadUri is null)
            throw new InvalidOperationException("Only downloaded templates can be removed.");

        var destination = GetArchiveCachePath(template, downloadDirectory);
        var gate = ArchiveLocks.GetOrAdd(destination, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            throw new IOException("This template is in use. Try again when the current operation finishes.");
        try
        {
            var directory = Path.Combine(downloadDirectory, "FluenityHubTemplates");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A linked template cache cannot be removed here.");
            File.Delete(destination);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        finally { gate.Release(); }
    }

    internal static async Task<JsonElement?> ReadArchiveMetadataAsync(string archive, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (await reader.GetNextEntryAsync(cancellationToken: deadline.Token).ConfigureAwait(false) is { } entry)
        {
            if (entry.Name is "package/package.json" or "package/ProjectData~/Packages/manifest.json" or "package/ProjectData/Packages/manifest.json" &&
                entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile &&
                entry.DataStream is { } metadata)
            {
                if (entry.Length > 1024 * 1024)
                    throw new InvalidDataException("The template package metadata is too large.");
                using var document = await JsonDocument.ParseAsync(metadata, cancellationToken: deadline.Token).ConfigureAwait(false);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("dependencies", out var dependencies) &&
                    dependencies.ValueKind == JsonValueKind.Object)
                    return document.RootElement.Clone();
            }
        }
        return null;
    }

    private static async Task<string> DownloadArchiveAsync(UnityProjectTemplate template, string downloadDirectory, string destination,
        IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (template.DownloadUri is not { } uri || template.ArchiveSha1 is not { } expectedHash)
            throw new InvalidDataException("The selected template has no verified Unity download.");
        if (await FindCachedArchiveAsync(template, downloadDirectory, cancellationToken).ConfigureAwait(false) is { } verified)
            return verified;

        NetworkConnectivityService.Current.EnsureCanAttemptInternet();
        var cacheDirectory = Path.Combine(downloadDirectory, "FluenityHubTemplates");
        Directory.CreateDirectory(cacheDirectory);
        var root = Path.GetPathRoot(cacheDirectory);
        if (root?.Length == 3 && new DriveInfo(root).AvailableFreeSpace < template.DownloadSize)
            throw new IOException("There is not enough free space in the configured download location.");
        var partial = destination + $".{Guid.NewGuid():N}.partial";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        try
        {
            using var response = await GetPackageAsync(uri, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != template.DownloadSize)
                throw new InvalidDataException("The template download size does not match Unity's catalog.");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                var lastPercent = -1;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > template.DownloadSize) throw new InvalidDataException("The Unity template download is larger than expected.");
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    var percent = (int)(received * 100 / template.DownloadSize);
                    if (percent != lastPercent) progress.Report(lastPercent = percent);
                }
                if (received != template.DownloadSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The template download did not match Unity's catalog. Try again.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            return destination;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Unity template download timed out. Try again.");
        }
        finally
        {
            try { File.Delete(partial); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine(ex); }
            catch (UnauthorizedAccessException ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }

    private static async Task<HttpResponseMessage> GetPackageAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("download.packages.unity.com" or "cdn.packages.unity.com"))
                throw new InvalidDataException("The template download was redirected outside Unity's package servers.");
            var response = await PackageClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) break;
            uri = new Uri(uri, location);
        }
        throw new InvalidDataException("Unity's package download could not be resolved.");
    }

    private static string? ReadSha1(string integrity)
    {
        if (!integrity.StartsWith("sha1-", StringComparison.Ordinal) || integrity.Length > 80) return null;
        Span<byte> buffer = stackalloc byte[40];
        if (!Convert.TryFromBase64String(integrity[5..], buffer, out var count)) return null;
        if (count == 20) return Convert.ToHexString(buffer[..count]);
        // Unity's catalog also sends base64-encoded hex instead of raw digest bytes.
        var hex = Encoding.ASCII.GetString(buffer[..count]);
        return count == 40 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static bool IsTemplatePackage(string value)
        => Regex.IsMatch(value, @"^com\.unity\.template\.[a-z0-9][a-z0-9.-]{0,100}\z");

    private static JsonElement Field(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Text(JsonElement element, string name)
        => Field(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
}
