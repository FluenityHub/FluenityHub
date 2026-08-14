namespace FluenityHub_WinUIHost.Models;

public sealed record UnityLicenseInfo(
    string Name,
    string Description,
    string Details);

public sealed record UnityLicenseSnapshot(
    bool IsClientAvailable,
    string ClientPath,
    string ClientVersion,
    IReadOnlyList<UnityLicenseInfo> Licenses,
    string StatusMessage);
