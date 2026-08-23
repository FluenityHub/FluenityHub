using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace FluenityHub_WinUIHost.Services;

public sealed class SourceControlService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static SourceControlService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FluenityHub", "1.0"));
        HttpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<(bool Success, string PrimaryOwner, List<string> Owners, string ErrorMessage)> AuthorizeTokenAsync(
        string provider,
        string token)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (false, string.Empty, [], NetworkConnectivityService.OfflineMessage);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, string.Empty, [], "Personal Access Token cannot be empty.");
        }

        token = token.Trim();
        provider = provider.ToLowerInvariant();

        try
        {
            if (provider == "github")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, string.Empty, [], $"GitHub Authorization failed ({response.StatusCode}). Check token permissions.");
                }

                var userJson = await response.Content.ReadAsStringAsync();
                var userNode = JsonNode.Parse(userJson)?.AsObject();
                var login = userNode?["login"]?.GetValue<string>() ?? string.Empty;

                var owners = new List<string>();
                if (!string.IsNullOrEmpty(login))
                {
                    owners.Add(login);
                }

                // Fetch Organizations
                try
                {
                    using var orgsReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/orgs");
                    orgsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    using var orgsResp = await HttpClient.SendAsync(orgsReq);
                    if (orgsResp.IsSuccessStatusCode)
                    {
                        var orgsJson = await orgsResp.Content.ReadAsStringAsync();
                        var orgsArray = JsonNode.Parse(orgsJson)?.AsArray();
                        if (orgsArray != null)
                        {
                            foreach (var item in orgsArray)
                            {
                                var orgLogin = item?["login"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(orgLogin) && !owners.Contains(orgLogin))
                                {
                                    owners.Add(orgLogin);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Non-critical organization fetch failure
                }

                return (true, login, owners, string.Empty);
            }
            else if (provider == "gitlab")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://gitlab.com/api/v4/user");
                request.Headers.Add("PRIVATE-TOKEN", token);

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, string.Empty, [], $"GitLab Authorization failed ({response.StatusCode}). Check token permissions.");
                }

                var userJson = await response.Content.ReadAsStringAsync();
                var userNode = JsonNode.Parse(userJson)?.AsObject();
                var username = userNode?["username"]?.GetValue<string>() ?? string.Empty;

                var owners = new List<string>();
                if (!string.IsNullOrEmpty(username))
                {
                    owners.Add(username);
                }

                return (true, username, owners, string.Empty);
            }

            return (false, string.Empty, [], $"Unsupported provider: {provider}");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, [],
                NetworkConnectivityService.Current.GetUserMessage(ex, ProviderDisplayName(provider)));
        }
    }

    public async Task<(bool Success, string RemoteUrl, string ErrorMessage)> CreateRemoteRepositoryAsync(
        string provider,
        string token,
        string primaryUser,
        string selectedOwner,
        string repoName,
        bool isPrivate,
        string description)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (false, string.Empty, NetworkConnectivityService.OfflineMessage);
        }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repoName))
        {
            return (false, string.Empty, "Token and repository name are required.");
        }

        token = token.Trim();
        repoName = repoName.Trim();
        provider = provider.ToLowerInvariant();

        try
        {
            if (provider == "github")
            {
                bool isUser = string.Equals(selectedOwner, primaryUser, StringComparison.OrdinalIgnoreCase);
                string endpoint = isUser
                    ? "https://api.github.com/user/repos"
                    : $"https://api.github.com/orgs/{selectedOwner}/repos";

                var payload = new JsonObject
                {
                    ["name"] = repoName,
                    ["private"] = isPrivate,
                    ["description"] = description,
                    ["auto_init"] = false
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await HttpClient.SendAsync(request);
                var respBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errNode = JsonNode.Parse(respBody)?.AsObject();
                    var msg = errNode?["message"]?.GetValue<string>() ?? response.ReasonPhrase;
                    return (false, string.Empty, $"GitHub API Error ({response.StatusCode}): {msg}");
                }

                var respNode = JsonNode.Parse(respBody)?.AsObject();
                var cloneUrl = respNode?["clone_url"]?.GetValue<string>()
                    ?? $"https://github.com/{selectedOwner}/{repoName}.git";

                return (true, cloneUrl, string.Empty);
            }
            else if (provider == "gitlab")
            {
                var payload = new JsonObject
                {
                    ["name"] = repoName,
                    ["visibility"] = isPrivate ? "private" : "public",
                    ["description"] = description,
                    ["initialize_with_readme"] = false
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://gitlab.com/api/v4/projects")
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("PRIVATE-TOKEN", token);

                using var response = await HttpClient.SendAsync(request);
                var respBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var errNode = JsonNode.Parse(respBody)?.AsObject();
                    var msg = errNode?["message"]?.GetValue<string>() ?? response.ReasonPhrase;
                    return (false, string.Empty, $"GitLab API Error ({response.StatusCode}): {msg}");
                }

                var respNode = JsonNode.Parse(respBody)?.AsObject();
                var cloneUrl = respNode?["http_url_to_repo"]?.GetValue<string>()
                    ?? $"https://gitlab.com/{selectedOwner}/{repoName}.git";

                return (true, cloneUrl, string.Empty);
            }

            return (false, string.Empty, $"Unsupported provider: {provider}");
        }
        catch (Exception ex)
        {
            return (false, string.Empty,
                NetworkConnectivityService.Current.GetUserMessage(ex, ProviderDisplayName(provider)));
        }
    }

    public static string Slugify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.ToLowerInvariant().Trim();
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (c is ' ' or '-' or '_')
            {
                if (sb.Length > 0 && sb[^1] != '-')
                {
                    sb.Append('-');
                }
            }
        }
        return sb.ToString().Trim('-');
    }

    public async Task<(bool Success, List<RepositoryItem> Repositories, string ErrorMessage)> GetRepositoriesAsync(string provider, string token)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (false, [], NetworkConnectivityService.OfflineMessage);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, [], "Personal Access Token cannot be empty.");
        }

        token = token.Trim();
        provider = provider.ToLowerInvariant();
        var repositories = new List<RepositoryItem>();

        try
        {
            if (provider == "github")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user/repos?per_page=100&sort=updated");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, [], $"GitHub API Error ({response.StatusCode}). Check token permissions.");
                }

                var json = await response.Content.ReadAsStringAsync();
                var array = JsonNode.Parse(json)?.AsArray();
                if (array != null)
                {
                    foreach (var node in array)
                    {
                        var name = node?["name"]?.GetValue<string>() ?? string.Empty;
                        var fullName = node?["full_name"]?.GetValue<string>() ?? name;
                        var cloneUrl = node?["clone_url"]?.GetValue<string>() ?? string.Empty;
                        var defaultBranch = node?["default_branch"]?.GetValue<string>() ?? "main";

                        if (!string.IsNullOrEmpty(name))
                        {
                            repositories.Add(new RepositoryItem(name, fullName, cloneUrl, defaultBranch));
                        }
                    }
                }

                return (true, repositories, string.Empty);
            }
            else if (provider == "gitlab")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://gitlab.com/api/v4/projects?membership=true&per_page=100&order_by=last_activity_at");
                request.Headers.Add("PRIVATE-TOKEN", token);

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, [], $"GitLab API Error ({response.StatusCode}). Check token permissions.");
                }

                var json = await response.Content.ReadAsStringAsync();
                var array = JsonNode.Parse(json)?.AsArray();
                if (array != null)
                {
                    foreach (var node in array)
                    {
                        var name = node?["name"]?.GetValue<string>() ?? string.Empty;
                        var fullName = node?["path_with_namespace"]?.GetValue<string>() ?? name;
                        var cloneUrl = node?["http_url_to_repo"]?.GetValue<string>() ?? string.Empty;
                        var defaultBranch = node?["default_branch"]?.GetValue<string>() ?? "main";

                        if (!string.IsNullOrEmpty(name))
                        {
                            repositories.Add(new RepositoryItem(name, fullName, cloneUrl, defaultBranch));
                        }
                    }
                }

                return (true, repositories, string.Empty);
            }

            return (false, [], $"Unsupported provider: {provider}");
        }
        catch (Exception ex)
        {
            return (false, [],
                NetworkConnectivityService.Current.GetUserMessage(ex, ProviderDisplayName(provider)));
        }
    }

    public async Task<(bool Success, List<BranchItem> Branches, string ErrorMessage)> GetBranchesAsync(string provider, string token, string repoFullName, string defaultBranch = "main")
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (false, [new BranchItem(defaultBranch, string.Empty)], NetworkConnectivityService.OfflineMessage);
        }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repoFullName))
        {
            return (false, [], "Token and repository name are required.");
        }

        token = token.Trim();
        provider = provider.ToLowerInvariant();
        var branches = new List<BranchItem>();

        try
        {
            if (provider == "github")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repoFullName}/branches?per_page=100");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, [new BranchItem(defaultBranch, string.Empty)], $"GitHub API Error ({response.StatusCode}).");
                }

                var json = await response.Content.ReadAsStringAsync();
                var array = JsonNode.Parse(json)?.AsArray();
                if (array != null)
                {
                    foreach (var node in array)
                    {
                        var name = node?["name"]?.GetValue<string>() ?? string.Empty;
                        var sha = node?["commit"]?["sha"]?.GetValue<string>() ?? string.Empty;

                        if (!string.IsNullOrEmpty(name))
                        {
                            branches.Add(new BranchItem(name, sha));
                        }
                    }
                }

                if (branches.Count == 0)
                {
                    branches.Add(new BranchItem(defaultBranch, string.Empty));
                }

                return (true, branches, string.Empty);
            }
            else if (provider == "gitlab")
            {
                string encodedPath = Uri.EscapeDataString(repoFullName);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://gitlab.com/api/v4/projects/{encodedPath}/repository/branches");
                request.Headers.Add("PRIVATE-TOKEN", token);

                using var response = await HttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, [new BranchItem(defaultBranch, string.Empty)], $"GitLab API Error ({response.StatusCode}).");
                }

                var json = await response.Content.ReadAsStringAsync();
                var array = JsonNode.Parse(json)?.AsArray();
                if (array != null)
                {
                    foreach (var node in array)
                    {
                        var name = node?["name"]?.GetValue<string>() ?? string.Empty;
                        var sha = node?["commit"]?["id"]?.GetValue<string>() ?? string.Empty;

                        if (!string.IsNullOrEmpty(name))
                        {
                            branches.Add(new BranchItem(name, sha));
                        }
                    }
                }

                if (branches.Count == 0)
                {
                    branches.Add(new BranchItem(defaultBranch, string.Empty));
                }

                return (true, branches, string.Empty);
            }

            return (false, [], $"Unsupported provider: {provider}");
        }
        catch (Exception ex)
        {
            return (false, [new BranchItem(defaultBranch, string.Empty)],
                NetworkConnectivityService.Current.GetUserMessage(ex, ProviderDisplayName(provider)));
        }
    }

    private static string ProviderDisplayName(string provider)
        => string.Equals(provider, "gitlab", StringComparison.OrdinalIgnoreCase)
            ? "GitLab"
            : "GitHub";
}

public sealed record RepositoryItem(string Name, string FullName, string CloneUrl, string DefaultBranch);
public sealed record BranchItem(string Name, string CommitSha);
