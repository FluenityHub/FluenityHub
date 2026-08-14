using System;
using System.Diagnostics;
using Windows.Security.Credentials;

namespace FluenityHub_WinUIHost.Services;

/// <summary>
/// Securely stores and retrieves user authentication tokens using the Windows 11 Credential Manager (PasswordVault).
/// </summary>
public static class CredentialService
{
    private const string ResourceName = "FluenityHub";
    private const string GitHubUserName = "GitHubToken";
    private const string GitLabUserName = "GitLabToken";
    private const string UnityCloudKeyIdUserName = "UnityCloudServiceAccountKeyId";
    private const string UnityCloudKeySecretUserName = "UnityCloudServiceAccountKeySecret";

    public static void SaveGitHubToken(string token)
    {
        SaveCredential(GitHubUserName, token);
    }

    public static string GetGitHubToken()
    {
        return GetCredential(GitHubUserName);
    }

    public static void SaveGitLabToken(string token)
    {
        SaveCredential(GitLabUserName, token);
    }

    public static string GetGitLabToken()
    {
        return GetCredential(GitLabUserName);
    }

    public static void RemoveGitHubToken()
    {
        RemoveCredential(GitHubUserName);
    }

    public static void RemoveGitLabToken()
    {
        RemoveCredential(GitLabUserName);
    }

    public static void RemoveSourceControlTokens()
    {
        RemoveGitHubToken();
        RemoveGitLabToken();
    }

    public static void SaveUnityCloudServiceAccount(string keyId, string keySecret)
    {
        try
        {
            SaveCredential(UnityCloudKeyIdUserName, keyId);
            SaveCredential(UnityCloudKeySecretUserName, keySecret);
        }
        catch
        {
            // Do not leave a partially updated service-account credential pair.
            RemoveCredential(UnityCloudKeyIdUserName);
            RemoveCredential(UnityCloudKeySecretUserName);
            throw;
        }
    }

    public static (string KeyId, string KeySecret) GetUnityCloudServiceAccount()
        => (
            GetCredential(UnityCloudKeyIdUserName),
            GetCredential(UnityCloudKeySecretUserName));

    public static void RemoveUnityCloudServiceAccount()
    {
        RemoveCredential(UnityCloudKeyIdUserName);
        RemoveCredential(UnityCloudKeySecretUserName);
    }

    private static void SaveCredential(string username, string password)
    {
        try
        {
            var vault = new PasswordVault();
            RemoveCredential(username);

            if (!string.IsNullOrWhiteSpace(password))
            {
                vault.Add(new PasswordCredential(ResourceName, username, password));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Windows Credential Locker rejected a FluenityHub credential write: {ex.GetType().Name}");
            throw new InvalidOperationException(
                "Windows Credential Locker could not save the credential.",
                ex);
        }
    }

    private static string GetCredential(string username)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(ResourceName, username);
            cred?.RetrievePassword();
            return cred?.Password ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RemoveCredential(string username)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(ResourceName, username);
            if (cred is not null)
            {
                vault.Remove(cred);
            }
        }
        catch
        {
            // Credential does not exist
        }
    }
}
