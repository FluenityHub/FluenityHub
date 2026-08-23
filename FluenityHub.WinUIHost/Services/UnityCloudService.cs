using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityCloudService
{
    private static readonly Uri ServiceRoot = new("https://services.api.unity.com/");
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = ServiceRoot,
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<UnityCloudProjectResult> GetProjectsAsync(
        string organizationId,
        string keyId,
        string keySecret,
        CancellationToken cancellationToken = default)
    {
        organizationId = organizationId.Trim();
        keyId = keyId.Trim();
        if (string.IsNullOrWhiteSpace(organizationId)
            || string.IsNullOrWhiteSpace(keyId)
            || string.IsNullOrWhiteSpace(keySecret))
        {
            return new(false, [], "Enter the organization ID, key ID, and key secret.");
        }

        if (organizationId.Length > 128
            || organizationId.Any(char.IsControl))
        {
            return new(false, [], "The Unity organization ID has an invalid format.");
        }

        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return new(false, [], NetworkConnectivityService.OfflineMessage);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"assets/v1/organizations/{Uri.EscapeDataString(organizationId)}/projects?Page=1&Limit=100");
        var basicCredential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{keyId}:{keySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredential);
        request.Headers.UserAgent.ParseAdd("FluenityHub/1.0");

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized =>
                        "Unity Cloud rejected the service-account credentials.",
                    System.Net.HttpStatusCode.Forbidden =>
                        "This service account does not have permission to read projects in the organization.",
                    System.Net.HttpStatusCode.NotFound =>
                        "The Unity organization was not found or is not available to this service account.",
                    _ => $"Unity Cloud returned HTTP {(int)response.StatusCode}."
                };
                return new(false, [], message);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var projects = ParseProjects(document.RootElement);
            return new(
                true,
                projects,
                projects.Count == 0
                    ? "Connected. This service account does not currently have access to any projects."
                    : $"Connected to {projects.Count} Unity Cloud project{(projects.Count == 1 ? string.Empty : "s")}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, [],
                "Unity Cloud did not respond in time. Check your connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            return new(false, [], NetworkConnectivityService.Current.GetUserMessage(
                ex,
                "Unity Cloud"));
        }
        catch (JsonException)
        {
            return new(false, [], "Unity Cloud returned an unexpected response.");
        }
    }

    private static IReadOnlyList<UnityCloudProjectInfo> ParseProjects(JsonElement root)
    {
        if (!root.TryGetProperty("projects", out var projectsElement)
            || projectsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var projects = new List<UnityCloudProjectInfo>();
        foreach (var project in projectsElement.EnumerateArray())
        {
            var id = ReadString(project, "id") ?? ReadString(project, "projectId");
            var name = ReadString(project, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            projects.Add(new UnityCloudProjectInfo(
                id,
                name,
                ReadString(project, "status") ?? "Available",
                ReadInt32(project, "userCount")));
        }

        return projects
            .OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result)
            ? result
            : 0;
}
