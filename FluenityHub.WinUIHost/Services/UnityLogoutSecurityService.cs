using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Applies Unity Hub's shared local-storage security preference after an
/// explicit Unity account logout succeeds.
/// </summary>
public sealed class UnityLogoutSecurityService
{
    private readonly UnityHubProjectSettingsService _settingsService = new();

    public bool ApplyAfterLogout(UnityCliAuthState state)
    {
        if (!state.IsCliAvailable
            || state.IsLoggedIn
            || !_settingsService.GetClearTokensOnLogout())
        {
            return false;
        }

        CredentialService.RemoveSourceControlTokens();
        return true;
    }
}
