namespace FluenityHub_WinUIHost.Models;

public sealed record UnityCloudProjectInfo(
    string Id,
    string Name,
    string Status,
    int UserCount);

public sealed record UnityCloudProjectResult(
    bool Succeeded,
    IReadOnlyList<UnityCloudProjectInfo> Projects,
    string Message);
