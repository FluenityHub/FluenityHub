using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed record UnityProjectShareLinkResult(
    bool Success,
    string? Link,
    string Message);

/// <summary>
/// Creates the tokenized project link consumed by Unity Hub's Editor deep-link flow.
/// The request contract mirrors Unity Hub's app-linking service and never persists
/// the shared Unity account token used to authorize it.
/// </summary>
public sealed class UnityProjectShareLinkService
{
    private static readonly Uri ServiceRoot = new("https://services.api.unity.com/");
    private static readonly Uri GatewayServiceRoot = new("https://services.unity.com/");
    private const string DeepLinkNamespace = "Unity.Cloud.DeepLinking.Editor";
    private const string VcsQueryKey = "Editor VCS info";
    private const int MaximumIdentifierLength = 512;
    private static readonly SemaphoreSlim CloudAuthenticationGate = new(1, 1);

    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = ServiceRoot,
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly HttpClient GatewayHttpClient = new()
    {
        BaseAddress = GatewayServiceRoot,
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static bool CanCreate(UnityProjectInfo project)
        => IsValidIdentifier(project.OrganizationId)
           && IsValidIdentifier(project.CloudProjectId)
           && TryBuildVcsInfo(project, out _);

    public async Task<UnityProjectShareLinkResult> CreateAsync(
        UnityProjectInfo project,
        CancellationToken cancellationToken = default)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return new(false, null, NetworkConnectivityService.OfflineMessage);
        }

        if (!IsValidIdentifier(project.OrganizationId)
            || !IsValidIdentifier(project.CloudProjectId))
        {
            return new(false, null, "Connect this project to Unity Cloud before copying a link.");
        }

        if (!TryBuildVcsInfo(project, out var vcsInfo))
        {
            return new(false, null, "Connect this project to GitHub, GitLab, or Unity Version Control before copying a link.");
        }

        var (token, authError) = await EnsureCloudAuthenticationAsync(cancellationToken);
        if (token is null)
        {
            return new(false, null, authError);
        }

        var organizationId = project.OrganizationId!;
        var cloudProjectId = project.CloudProjectId!;
        var resolvedVcsInfo = vcsInfo!;

        var vcsJson = new JsonObject
        {
            ["Vcs"] = resolvedVcsInfo.Vcs,
            ["Url"] = resolvedVcsInfo.Url,
            ["Branch"] = resolvedVcsInfo.Branch,
            ["Head"] = resolvedVcsInfo.Head,
            ["Repo"] = resolvedVcsInfo.Repo,
            ["ProjectPath"] = resolvedVcsInfo.ProjectPath
        }.ToJsonString(AppJsonContext.Default.Options);
        var queryArguments = $"{System.Net.WebUtility.UrlEncode(VcsQueryKey)}={System.Net.WebUtility.UrlEncode(vcsJson)}";
        var requestBody = new JsonObject
        {
            ["ResourceId"] = $"{organizationId},{cloudProjectId}",
            ["ResourceType"] = "Project",
            ["QueryArguments"] = queryArguments
        }.ToJsonString(AppJsonContext.Default.Options);

        using var request = new HttpRequestMessage(HttpMethod.Post, "app-linking/v1/links")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.UnityTokenValue);
        request.Headers.Add("X-Client-ID", "unity-hub");
        request.Headers.UserAgent.ParseAdd("FluenityHub/1.0");

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, null, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized =>
                        "Unity sign-in expired. Sign in again and retry.",
                    System.Net.HttpStatusCode.Forbidden =>
                        "Your Unity account cannot create a link for this Cloud project.",
                    _ => $"Unity could not create the link (HTTP {(int)response.StatusCode})."
                });
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("Url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || !TryExtractToken(urlElement.GetString(), out var linkToken))
            {
                return new(false, null, "Unity returned an invalid project link.");
            }

            var link = new Uri(
                ServiceRoot,
                $"app-linking/v1/hub/editor/{DeepLinkNamespace}/{Uri.EscapeDataString(linkToken)}").AbsoluteUri;
            return new(true, link, "Link copied to clipboard.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null,
                "Unity did not create the link in time. Check your connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            return new(false, null, NetworkConnectivityService.Current.GetUserMessage(
                ex,
                "Unity's link service"));
        }
        catch (JsonException)
        {
            return new(false, null, "Unity returned an invalid project link.");
        }
    }

    /// <summary>
    /// Gets a current Unity gateway token for a user-requested share action.
    /// Unity Cloud app-linking cannot use the Editor OAuth token, so this
    /// method never falls back to it. A gate prevents concurrent menu actions
    /// from starting competing browser sign-in sessions.
    /// </summary>
    private async Task<(UnitySharedAccessToken? Token, string ErrorMessage)>
        EnsureCloudAuthenticationAsync(CancellationToken cancellationToken)
    {
        if (TryGetUsableCloudToken(out var token, out var lastError))
        {
            return (token, string.Empty);
        }

        await CloudAuthenticationGate.WaitAsync(cancellationToken);
        try
        {
            // A previous request may have completed the browser flow while
            // this one was waiting for the gate.
            if (TryGetUsableCloudToken(out token, out lastError))
            {
                return (token, string.Empty);
            }

            if (UnitySharedAuthService.TryIsHubAccountActive(out var hubOwnsSession, out _)
                && hubOwnsSession)
            {
                return (null,
                    "Unity Hub is refreshing the active account. Wait a moment and retry so FluenityHub does not start a competing sign-in session.");
            }

            var authService = new UnityCliAuthService();
            UnityCliAuthState state;
            try
            {
                state = await authService.GetStatusAsync(cancellationToken);
                if (!state.IsLoggedIn)
                {
                    state = await authService.LoginAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return (null, "Creating the Unity project link was canceled.");
            }

            if (!state.IsLoggedIn)
            {
                return (null, string.IsNullOrWhiteSpace(state.Message)
                    ? "Sign in to Unity and retry creating the project link."
                    : state.Message);
            }

            return await ExchangeUnityGatewayTokenAsync(cancellationToken);
        }
        finally
        {
            CloudAuthenticationGate.Release();
        }
    }

    /// <summary>
    /// Mirrors Unity Hub's gateway-token exchange for a fresh Unity CLI OAuth
    /// session. The returned token is held only for the current link request;
    /// FluenityHub never writes it to Credential Manager or settings.
    /// </summary>
    private async Task<(UnitySharedAccessToken? Token, string ErrorMessage)>
        ExchangeUnityGatewayTokenAsync(CancellationToken cancellationToken)
    {
        if (!UnitySharedAuthService.TryGetActiveAccessToken(out var sharedToken, out var errorMessage)
            || sharedToken is null)
        {
            return (null, string.IsNullOrWhiteSpace(errorMessage)
                ? "Unity sign-in could not be read. Sign in again and retry."
                : errorMessage);
        }

        if (!UnitySharedAuthService.IsAccessTokenUsable(sharedToken))
        {
            return (null, "Unity sign-in expired. Sign in again and retry.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/v1/genesis-token-exchange/unity")
        {
            Content = new StringContent(
                new JsonObject { ["token"] = sharedToken.Value }
                    .ToJsonString(AppJsonContext.Default.Options),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", "hub");
        request.Headers.Date = DateTimeOffset.UtcNow;

        try
        {
            using var response = await GatewayHttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized =>
                        "Unity rejected the Cloud sign-in. Sign in again and retry.",
                    System.Net.HttpStatusCode.Forbidden =>
                        "Your Unity account cannot access Unity Cloud right now.",
                    _ => $"Unity could not refresh Cloud authentication (HTTP {(int)response.StatusCode})."
                });
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenElement.GetString())
                || !UnitySharedAuthService.TryValidateUnityGatewayToken(
                    tokenElement.GetString()!,
                    sharedToken.Account.ForeignKey,
                    out var expiration))
            {
                return (null, "Unity returned an invalid Cloud credential. Sign in again and retry.");
            }

            var exchangedToken = new UnitySharedAccessToken(
                sharedToken.Value,
                sharedToken.Expiration,
                tokenElement.GetString(),
                expiration,
                sharedToken.Account);
            return UnitySharedAuthService.HasUsableUnityGatewayToken(exchangedToken)
                ? (exchangedToken, string.Empty)
                : (null, "Unity returned an expired Cloud credential. Sign in again and retry.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "Unity did not refresh Cloud authentication in time. Check your connection and retry.");
        }
        catch (HttpRequestException ex)
        {
            return (null, NetworkConnectivityService.Current.GetUserMessage(
                ex,
                "Unity Cloud authentication"));
        }
        catch (JsonException)
        {
            return (null, "Unity returned an invalid Cloud credential. Sign in again and retry.");
        }
    }

    private static bool TryGetUsableCloudToken(
        out UnitySharedAccessToken? token,
        out string errorMessage)
    {
        token = null;
        errorMessage = string.Empty;
        if (!UnitySharedAuthService.TryGetActiveAccessToken(out var sharedToken, out errorMessage)
            || sharedToken is null)
        {
            return false;
        }

        if (!UnitySharedAuthService.IsAccessTokenUsable(sharedToken))
        {
            errorMessage = "Unity sign-in expired. Sign in again to continue.";
            return false;
        }

        // Unity Hub's app-linking service authenticates with the Unity gateway
        // token, not the OAuth access token used by Editor/profile APIs.
        if (!UnitySharedAuthService.HasUsableUnityGatewayToken(sharedToken))
        {
            errorMessage = "Unity Cloud authentication is unavailable. Sign in again to continue.";
            return false;
        }

        token = sharedToken;
        return true;
    }

    private static bool TryBuildVcsInfo(UnityProjectInfo project, out VcsInfo? vcsInfo)
    {
        vcsInfo = null;
        var provider = project.SourceControlProvider?.Trim();
        if (string.Equals(provider, SourceControlDetectionService.GitHubProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, SourceControlDetectionService.GitLabProvider, StringComparison.OrdinalIgnoreCase))
        {
            var url = NormalizeGitRemote(project.SourceControlRemoteUrl)
                      ?? BuildConfiguredGitRemote(project, provider!);
            if (url is null)
            {
                return false;
            }

            vcsInfo = new(
                "git",
                url,
                project.GitBranch ?? project.SourceControlDetail ?? string.Empty,
                GitService.GetHeadCommit(project.Path) ?? string.Empty,
                string.Empty,
                project.ProjectPathInsideRepository ?? string.Empty);
            return true;
        }

        if (!string.Equals(provider, SourceControlDetectionService.UnityVersionControlProvider, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(project.ConfiguredSourceControlOrganization)
            || string.IsNullOrWhiteSpace(project.ConfiguredSourceControlRepository))
        {
            return false;
        }

        vcsInfo = new(
            "plastic",
            $"{project.ConfiguredSourceControlRepository}@{project.ConfiguredSourceControlOrganization}@cloud",
            project.SourceControlDetail ?? string.Empty,
            project.SourceControlRevision ?? string.Empty,
            string.Empty,
            project.ProjectPathInsideRepository ?? string.Empty);
        return true;
    }

    private static string? BuildConfiguredGitRemote(UnityProjectInfo project, string provider)
    {
        if (string.IsNullOrWhiteSpace(project.ConfiguredSourceControlOrganization)
            || string.IsNullOrWhiteSpace(project.ConfiguredSourceControlRepository))
        {
            return null;
        }

        var host = string.Equals(provider, SourceControlDetectionService.GitHubProvider, StringComparison.OrdinalIgnoreCase)
            ? "github.com"
            : "gitlab.com";
        return $"https://{host}/{project.ConfiguredSourceControlOrganization.Trim('/')}/{project.ConfiguredSourceControlRepository.Trim('/')}";
    }

    private static string? NormalizeGitRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote.Length > 4096)
        {
            return null;
        }

        var normalized = remote.Trim();
        var scpMatch = System.Text.RegularExpressions.Regex.Match(
            normalized,
            "^git@([^:]+):(.+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (scpMatch.Success)
        {
            normalized = $"https://{scpMatch.Groups[1].Value}/{scpMatch.Groups[2].Value}";
        }
        else if (normalized.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                 || normalized.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized[(normalized.IndexOf("//", StringComparison.Ordinal) + 2)..];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var builder = new UriBuilder(Uri.UriSchemeHttps, uri.Host)
        {
            Path = uri.AbsolutePath,
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static bool IsValidIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= MaximumIdentifierLength
           && !value.Contains(',')
           && !value.Any(char.IsControl);

    private static bool TryExtractToken(string? url, out string token)
    {
        token = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        token = uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
        return token.Length > 0 && token.Length <= 4096 && !token.Any(char.IsControl);
    }

    private sealed record VcsInfo(
        string Vcs,
        string Url,
        string Branch,
        string Head,
        string Repo,
        string ProjectPath);
}
