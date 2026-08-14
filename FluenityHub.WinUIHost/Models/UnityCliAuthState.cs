namespace FluenityHub_WinUIHost.Models;

public sealed record UnityCliAuthState(
    bool IsCliAvailable,
    bool IsLoggedIn,
    string DisplayName,
    string Email,
    string Mode,
    string Message)
{
    public string SessionState { get; init; } = string.Empty;

    public bool RequiresReauthentication
        => IsCliAvailable
           && !IsLoggedIn
           && SessionState.Equals("stale", StringComparison.OrdinalIgnoreCase);
}
