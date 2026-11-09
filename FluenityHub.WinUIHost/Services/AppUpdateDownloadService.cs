using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Xml;
using System.Xml.Linq;

namespace FluenityHub_WinUIHost.Services;

internal static class AppUpdateDownloadService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };

    static AppUpdateDownloadService() => Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluenityHub", AppUpdateService.CurrentVersion));

    public static async Task<string> DownloadAsync(AppUpdateInfo update, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (update.DownloadUrl is not { } url || !AppUpdateService.IsTrustedAssetUrl(url)
            || update.AssetName is not { } name || Path.GetFileName(name) != name
            || update.Sha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit)
            || update.DownloadSize <= 0 || update.DownloadSize > AppUpdateService.MaximumDownloadSize)
            throw new InvalidDataException("This release does not have a verified download.");

        NetworkConnectivityService.Current.EnsureCanAttemptInternet();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        cancellationToken = deadline.Token;
        var directory = Path.Combine(new UnityHubLocationSettingsService().GetDownloadLocation(), "FluenityHubUpdates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, name);
        var partial = destination + ".partial";
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != update.DownloadSize)
                throw new InvalidDataException("The update download size does not match the release.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                long total = 0;
                var reportedPercentage = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > update.DownloadSize) throw new InvalidDataException("The update is larger than the release asset.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    var percentage = (int)(total * 100 / update.DownloadSize);
                    if (percentage != reportedPercentage)
                    {
                        reportedPercentage = percentage;
                        progress.Report(percentage);
                    }
                }
                if (total != update.DownloadSize || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(digest)))
                    throw new InvalidDataException("The update download failed its SHA-256 check. It was not installed.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => RequireExpectedIdentityAndSigner(partial, update.LatestVersion), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    internal static void RequireExpectedIdentityAndSigner(string path, string releaseVersion)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifest = SingleEntry(archive, "AppxManifest.xml");
        if (manifest.Length > 65536) throw new InvalidDataException("The update manifest is too large.");
        using var stream = manifest.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
        var document = XDocument.Load(reader);
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = document.Root?.Element(ns + "Identity");
        var expectedCore = releaseVersion.Split('-', '+')[0];
        if (identity?.Attribute("Name")?.Value != AppUpdateService.PackageName
            || identity.Attribute("Publisher")?.Value != AppUpdateService.Publisher
            || identity.Attribute("ProcessorArchitecture")?.Value != "x64"
            || !Version.TryParse(identity.Attribute("Version")?.Value, out var version)
            || !Version.TryParse(expectedCore, out var expected)
            || version.Major != expected.Major || version.Minor != expected.Minor || version.Build != expected.Build)
            throw new InvalidDataException("The update package identity, architecture, or version does not match FluenityHub.");

        var signature = SingleEntry(archive, "AppxSignature.p7x");
        if (signature.Length > 65536) throw new InvalidDataException("The update signature is too large.");
        using var signatureStream = signature.Open();
        using var memory = new MemoryStream();
        signatureStream.CopyTo(memory);
        RequireOriginalSigner(memory.ToArray());
        // Windows deployment verifies the package signature and block map before installation.
    }

    private static ZipArchiveEntry SingleEntry(ZipArchive archive, string name)
    {
        var entries = archive.Entries.Where(entry => string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return entries.Length == 1 ? entries[0] : throw new InvalidDataException($"The update must contain one {name}.");
    }

    internal static void RequireOriginalSigner(byte[] signature)
    {
        if (signature.Length < 5 || !signature.AsSpan(0, 4).SequenceEqual("PKCX"u8))
            throw new InvalidDataException("The update has no valid MSIX signature.");
        try
        {
            var cms = new SignedCms();
            cms.Decode(signature.AsSpan(4));
            if (cms.SignerInfos.Count != 1)
                throw new InvalidDataException("The update must have one package signer.");
            var signer = cms.SignerInfos[0];
            using var certificate = signer.Certificate;
            if (certificate?.GetCertHashString(HashAlgorithmName.SHA256) != AppUpdateService.SigningCertificateSha256)
                throw new InvalidDataException("This update was not signed with the original FluenityHub certificate.");
            signer.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("The MSIX signing certificate could not be verified.", ex);
        }
    }
}
