using System.Net.Http.Headers;
using System.Text.Json;

namespace FluenityHub_WinUIHost.Services;

internal static class RemoteRepositoryInfoService
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<string> ReadSizeAsync(string? remote, CancellationToken cancellationToken)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet) return NetworkConnectivityService.OfflineMessage;
        if (string.IsNullOrWhiteSpace(remote)) return "No remote repository is configured.";
        if (remote.StartsWith("git@", StringComparison.Ordinal)) remote = "ssh://" + remote[4..].Replace(':', '/');
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh")
            || uri.Host is not ("github.com" or "gitlab.com") || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme == "https" && !string.IsNullOrEmpty(uri.UserInfo)))
            return "Repository size is available for GitHub and GitLab.";
        var repository = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];
        repository = GitCredentialManagerService.NormalizeScope(repository);
        if (repository.Split('/').Length < 2 || (uri.Host == "github.com" && repository.Split('/').Length != 2))
            return "The repository URL is invalid.";
        var provider = uri.Host == "github.com" ? "github" : "gitlab";
        using var request = new HttpRequestMessage(HttpMethod.Get, provider == "github"
            ? $"https://api.github.com/repos/{string.Join('/', repository.Split('/').Select(Uri.EscapeDataString))}"
            : $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(repository)}?statistics=true");
        request.Headers.UserAgent.ParseAdd("FluenityHub/1.0");
        var credential = await GitCredentialManagerService.GetAsync(provider, repository, false, cancellationToken);
        var token = credential?.Password ?? (provider == "github" ? CredentialService.GetGitHubToken() : CredentialService.GetGitLabToken());
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return $"Repository size is unavailable (HTTP {(int)response.StatusCode}).";
        await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return "The provider returned invalid repository metadata.";
        long bytes;
        if (provider == "github" && root.TryGetProperty("size", out var size) && size.TryGetInt64(out var kib)) bytes = checked(kib * 1024);
        else if (provider == "gitlab" && root.TryGetProperty("statistics", out var stats)
            && stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("repository_size", out var repositorySize) && repositorySize.TryGetInt64(out var value)) bytes = value;
        else return "The provider did not return repository size for this account.";
        return $"Remote repository size: {bytes / 1048576d:0.##} MiB (reported by {uri.Host})";
    }
}
