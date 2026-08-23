using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FluenityHub_WinUIHost.Services;

internal sealed record UnitySharedAccount(
    string ForeignKey,
    string DisplayName,
    string Email,
    string PrimaryOrganization);

internal sealed record UnitySharedAccessToken(
    string Value,
    double? Expiration,
    string? UnityTokenValue,
    double? UnityTokenExpiration,
    UnitySharedAccount Account,
    string? RefreshToken = null,
    double? RefreshTokenExpiration = null);

/// <summary>
/// Reads the active Unity CLI account from Unity's shared account directory and
/// retrieves its OAuth token from Windows Credential Manager. Secrets are never
/// persisted by FluenityHub and are never included in diagnostic messages.
/// </summary>
internal static class UnitySharedAuthService
{
    private const string SharedConsumer = "__shared__";
    private const string HubConsumer = "hub";
    private const string CliConsumer = "cli";
    private const string CredentialSuffix = ".unity";
    private const string LegacyCombinedTokensCredential = "UnityHub/combinedTokens";
    private const string ChunkManifestMarker = "__unityHubKeyringChunkedV1__";
    private const int MaximumChunkCount = 16_384;
    private const uint CredentialTypeGeneric = 1;
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private static readonly TimeSpan TokenRefreshWindow = TimeSpan.FromMinutes(5);

    private static string AccountsDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub",
        "accounts.db");

    public static bool TryGetActiveAccount(
        out UnitySharedAccount? account,
        out string errorMessage)
    {
        account = null;
        errorMessage = string.Empty;

        try
        {
            account = ReadActiveAccount();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            errorMessage = $"Unable to access Unity's shared account state: {ex.Message}";
            return false;
        }
    }

    public static bool TryIsHubAccountActive(
        out bool isActive,
        out string errorMessage)
    {
        isActive = false;
        errorMessage = string.Empty;

        try
        {
            if (!File.Exists(AccountsDatabasePath))
            {
                return true;
            }

            var pathBytes = Utf8Z(AccountsDatabasePath);
            var result = sqlite3_open_v2(pathBytes, out var database, 0x00000001, IntPtr.Zero);
            if (result != SqliteOk || database == IntPtr.Zero)
            {
                if (database != IntPtr.Zero)
                {
                    sqlite3_close(database);
                }

                throw new IOException("Unity's shared account directory could not be opened.");
            }

            try
            {
                var hub = QueryActiveForeignKey(database, HubConsumer, out var hasHubRow);
                // The database row survives Hub restarts. Treat it as an active
                // Hub session only while the Hub process is actually running.
                isActive = hasHubRow
                    && !string.IsNullOrWhiteSpace(hub)
                    && IsUnityHubRunning();
                return true;
            }
            finally
            {
                sqlite3_close(database);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            errorMessage = $"Unable to access Unity Hub's account state: {ex.Message}";
            return false;
        }
    }

    private static bool IsUnityHubRunning()
    {
        try
        {
            return Process.GetProcesses().Any(process =>
            {
                try
                {
                    return process.ProcessName.Equals("Unity Hub", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Equals("UnityHub", StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    process.Dispose();
                }
            });
        }
        catch
        {
            // If process enumeration is unavailable, do not turn stale disk
            // state into a permanent launch block.
            return false;
        }
    }

    public static bool TryGetActiveAccessToken(
        out UnitySharedAccessToken? token,
        out string errorMessage)
    {
        token = null;
        errorMessage = string.Empty;

        try
        {
            var account = ReadActiveAccount();
            if (account is null)
            {
                errorMessage = "Unity CLI is not signed in. Sign in from the account menu and try again.";
                return false;
            }

            if (!IsValidForeignKey(account.ForeignKey))
            {
                errorMessage = "Unity's active account identifier is invalid.";
                return false;
            }

            var credentialAccount = $"auth-tokens:{account.ForeignKey}";
            var payload = ReadChunkedCredential(credentialAccount);
            if (string.IsNullOrWhiteSpace(payload))
            {
                errorMessage = "The active Unity CLI credential could not be read. Sign in again and retry.";
                return false;
            }

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("accessToken", out var accessTokenElement)
                || accessTokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
            {
                errorMessage = "The active Unity CLI credential does not contain an access token. Sign in again and retry.";
                return false;
            }

            double? expiration = null;
            if (document.RootElement.TryGetProperty("accessTokenExpiration", out var expirationElement)
                && expirationElement.ValueKind == JsonValueKind.Number
                && expirationElement.TryGetDouble(out var expirationValue))
            {
                expiration = expirationValue;
            }

            string? unityToken = null;
            if (document.RootElement.TryGetProperty("unityToken", out var unityTokenElement)
                && unityTokenElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(unityTokenElement.GetString()))
            {
                unityToken = unityTokenElement.GetString();
            }

            double? unityTokenExpiration = null;
            if (document.RootElement.TryGetProperty("unityTokenExpiration", out var unityExpirationElement)
                && TryReadExpiration(unityExpirationElement, out var unityExpirationValue))
            {
                unityTokenExpiration = unityExpirationValue;
            }

            string? refreshToken = null;
            if (document.RootElement.TryGetProperty("refreshToken", out var refreshTokenElement)
                && refreshTokenElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(refreshTokenElement.GetString()))
            {
                refreshToken = refreshTokenElement.GetString();
            }

            double? refreshTokenExpiration = null;
            if (document.RootElement.TryGetProperty("refreshTokenExpiration", out var refreshTokenExpElement)
                && TryReadExpiration(refreshTokenExpElement, out var refreshTokenExpValue))
            {
                refreshTokenExpiration = refreshTokenExpValue;
            }

            // Unity CLI 1.0.0-beta.5 stores only OAuth/refresh tokens in its
            // shared credential. Unity Hub keeps the gateway token required by
            // Cloud app-linking in its legacy combined-token credential. The
            // OAuth value can rotate between the two consumers, so correlate
            // the Hub gateway token to the active stable Unity account instead
            // of requiring the ephemeral OAuth values to remain identical.
            if ((string.IsNullOrWhiteSpace(unityToken)
                 || !IsTokenUsable(unityTokenExpiration, requireExpiration: false))
                && TryReadHubGatewayTokenForAccount(
                    accessTokenElement.GetString()!,
                    account.ForeignKey,
                    out var hubUnityToken,
                    out var hubUnityTokenExpiration))
            {
                unityToken = hubUnityToken;
                unityTokenExpiration = hubUnityTokenExpiration;
            }

            token = new UnitySharedAccessToken(
                accessTokenElement.GetString()!,
                expiration,
                unityToken,
                unityTokenExpiration,
                account,
                refreshToken,
                refreshTokenExpiration);
            return true;
        }
        catch (JsonException)
        {
            errorMessage = "Unity's stored sign-in credential is damaged. Sign in again and retry.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            errorMessage = $"Unable to access the Unity CLI sign-in: {ex.Message}";
            return false;
        }
    }

    private static bool TryReadHubGatewayTokenForAccount(
        string activeAccessToken,
        string activeAccountForeignKey,
        out string? unityToken,
        out double? unityTokenExpiration)
    {
        unityToken = null;
        unityTokenExpiration = null;

        try
        {
            var payload = ReadCredentialUtf8(LegacyCombinedTokensCredential);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("unityToken", out var unityTokenElement)
                || unityTokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(unityTokenElement.GetString()))
            {
                return false;
            }

            var legacyGatewayToken = unityTokenElement.GetString()!;
            if (!root.TryGetProperty("accessToken", out var accessTokenElement)
                || accessTokenElement.ValueKind != JsonValueKind.String
                || (!TokensMatch(activeAccessToken, accessTokenElement.GetString())
                    && !GatewayTokenMatchesAccount(legacyGatewayToken, activeAccountForeignKey)))
            {
                return false;
            }

            unityToken = legacyGatewayToken;
            if (root.TryGetProperty("unityTokenExpiration", out var expirationElement)
                && TryReadExpiration(expirationElement, out var expiration))
            {
                unityTokenExpiration = expiration;
            }

            return true;
        }
        catch (JsonException)
        {
            // Legacy Hub data is optional. Ignore an invalid record and allow
            // the normal CLI authentication path to report a safe error.
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool GatewayTokenMatchesAccount(string gatewayToken, string activeAccountForeignKey)
        => TryValidateUnityGatewayToken(gatewayToken, activeAccountForeignKey, out _);

    /// <summary>
    /// Validates that a Unity gateway JWT belongs to the active Unity account
    /// and returns its expiry as Unix milliseconds. The signature remains
    /// authoritative at Unity's service boundary; this local check prevents a
    /// stale gateway token for another account from being selected here.
    /// </summary>
    public static bool TryValidateUnityGatewayToken(
        string gatewayToken,
        string activeAccountForeignKey,
        out double? expiration)
    {
        expiration = null;
        const int MaximumGatewayTokenLength = 32_768;
        if (gatewayToken.Length is 0 or > MaximumGatewayTokenLength
            || string.IsNullOrWhiteSpace(activeAccountForeignKey))
        {
            return false;
        }

        var segments = gatewayToken.Split('.');
        if (segments.Length != 3 || string.IsNullOrWhiteSpace(segments[1]))
        {
            return false;
        }

        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2:
                payload += "==";
                break;
            case 3:
                payload += "=";
                break;
            case 1:
                return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (!document.RootElement.TryGetProperty("sub", out var subjectElement)
                    || subjectElement.ValueKind != JsonValueKind.String
                    || !TokensMatch(activeAccountForeignKey, subjectElement.GetString())
                    || !document.RootElement.TryGetProperty("exp", out var expirationElement)
                    || expirationElement.ValueKind != JsonValueKind.Number
                    || !expirationElement.TryGetDouble(out var expirationSeconds)
                    || double.IsNaN(expirationSeconds)
                    || double.IsInfinity(expirationSeconds)
                    || expirationSeconds <= 0
                    || expirationSeconds > long.MaxValue / 1_000d)
                {
                    return false;
                }

                expiration = expirationSeconds * 1_000d;
                return true;
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TokensMatch(string expected, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(candidateBytes);
        }
    }

    private static bool TryReadExpiration(JsonElement element, out double expiration)
    {
        expiration = default;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out expiration);
        }

        return element.ValueKind == JsonValueKind.String
               && double.TryParse(
                   element.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out expiration);
    }

    /// <summary>
    /// Determines whether the Editor OAuth token can safely be used without
    /// making a request that is expected to fail during its refresh window.
    /// </summary>
    public static bool IsAccessTokenUsable(UnitySharedAccessToken token)
        => IsTokenUsable(token.Expiration, requireExpiration: true);

    /// <summary>
    /// Determines whether the Unity gateway token required by Cloud services
    /// is present and has not expired. Older CLI payloads may omit the gateway
    /// expiration, so a present token without that optional value remains
    /// usable and the service can authoritatively reject it if necessary.
    /// </summary>
    public static bool HasUsableUnityGatewayToken(UnitySharedAccessToken token)
        => !string.IsNullOrWhiteSpace(token.UnityTokenValue)
           && IsTokenUsable(token.UnityTokenExpiration, requireExpiration: false);

    /// <summary>
    /// Determines whether the stored refresh token is present and within its 30-day validity window.
    /// </summary>
    public static bool HasUsableRefreshToken(UnitySharedAccessToken token)
        => !string.IsNullOrWhiteSpace(token.RefreshToken)
           && IsTokenUsable(token.RefreshTokenExpiration, requireExpiration: false);

    private static readonly HttpClient AuthHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Silently renews the OAuth access token using the stored 30-day refresh token,
    /// matching Unity Hub's official cloud core refresh endpoint.
    /// </summary>
    public static async Task<(UnitySharedAccessToken? Token, string ErrorMessage)> RefreshOAuthTokenAsync(
        CancellationToken cancellationToken = default)
    {
        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return (null, NetworkConnectivityService.OfflineMessage);
        }

        if (!TryGetActiveAccount(out var account, out var accountError) || account is null)
        {
            return (null, string.IsNullOrWhiteSpace(accountError) ? "Unity CLI is not signed in." : accountError);
        }

        var credentialAccount = $"auth-tokens:{account.ForeignKey}";
        var payload = ReadChunkedCredential(credentialAccount);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, "No stored Unity authentication tokens found to refresh.");
        }

        string? refreshToken = null;
        double? refreshTokenExpiration = null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("refreshToken", out var refreshElement)
                && refreshElement.ValueKind == JsonValueKind.String)
            {
                refreshToken = refreshElement.GetString();
            }

            if (root.TryGetProperty("refreshTokenExpiration", out var refreshExpElement)
                && TryReadExpiration(refreshExpElement, out var refreshExpValue))
            {
                refreshTokenExpiration = refreshExpValue;
            }
        }
        catch (JsonException)
        {
            return (null, "Stored authentication token format is damaged.");
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return (null, "No refresh token available to renew the session.");
        }

        if (!IsTokenUsable(refreshTokenExpiration, requireExpiration: false))
        {
            return (null, "The 30-day refresh session has expired. Please sign in again.");
        }

        try
        {
            var requestBody = new System.Text.Json.Nodes.JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://core.cloud.unity3d.com/api/login/refresh")
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd("hub");

            using var response = await AuthHttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"Unity authentication refresh failed with HTTP {(int)response.StatusCode}.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var respDoc = JsonDocument.Parse(responseJson);
            var respRoot = respDoc.RootElement;

            if (!respRoot.TryGetProperty("access_token", out var newAccessElem)
                || newAccessElem.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(newAccessElem.GetString()))
            {
                return (null, "Invalid response from Unity token refresh endpoint.");
            }

            var newAccessToken = newAccessElem.GetString()!;
            double newAccessExpiresInSeconds = 3600;
            if (respRoot.TryGetProperty("expires_in", out var expiresInElem)
                && expiresInElem.TryGetDouble(out var expiresInVal))
            {
                newAccessExpiresInSeconds = expiresInVal;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var newAccessTokenExpiration = (double)(nowMs + (long)(newAccessExpiresInSeconds * 1000));

            var newRefreshToken = refreshToken;
            if (respRoot.TryGetProperty("refresh_token", out var newRefreshElem)
                && newRefreshElem.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(newRefreshElem.GetString()))
            {
                newRefreshToken = newRefreshElem.GetString()!;
            }

            // 30 days default validity for refresh token
            var newRefreshTokenExpiration = (double)(nowMs + 2_592_000_000L);

            var updatedPayloadObj = new System.Text.Json.Nodes.JsonObject
            {
                ["accessToken"] = newAccessToken,
                ["accessTokenExpiration"] = newAccessTokenExpiration,
                ["refreshToken"] = newRefreshToken,
                ["refreshTokenExpiration"] = newRefreshTokenExpiration
            };

            // Write back to Windows Credential Manager
            WriteChunkedCredential(credentialAccount, updatedPayloadObj.ToJsonString());

            var token = new UnitySharedAccessToken(
                newAccessToken,
                newAccessTokenExpiration,
                null,
                null,
                account,
                newRefreshToken,
                newRefreshTokenExpiration);

            return (token, string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or Win32Exception)
        {
            return (null, $"Error refreshing token: {ex.Message}");
        }
    }

    private static bool IsTokenUsable(double? rawExpiration, bool requireExpiration)
    {
        if (rawExpiration is not double value)
        {
            return !requireExpiration;
        }

        if (double.IsNaN(value)
            || double.IsInfinity(value)
            || value < long.MinValue
            || value > long.MaxValue)
        {
            return false;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)value)
                   > DateTimeOffset.UtcNow + TokenRefreshWindow;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static UnitySharedAccount? ReadActiveAccount()
    {
        if (!File.Exists(AccountsDatabasePath))
        {
            return null;
        }

        var pathBytes = Utf8Z(AccountsDatabasePath);
        var result = sqlite3_open_v2(pathBytes, out var database, 0x00000001, IntPtr.Zero);
        if (result != SqliteOk || database == IntPtr.Zero)
        {
            if (database != IntPtr.Zero)
            {
                sqlite3_close(database);
            }

            throw new IOException("Unity's shared account directory could not be opened.");
        }

        try
        {
            // FluenityHub observes both supported Unity consumers. Unity Hub
            // owns the account only while its process is running; its database
            // row remains after shutdown and can otherwise point to an older
            // credential than the session authenticated by Unity CLI.
            var hub = QueryActiveForeignKey(database, HubConsumer, out var hasHubRow);
            if (!string.IsNullOrWhiteSpace(hub) && IsUnityHubRunning())
            {
                return QueryAccount(database, hub);
            }

            var cli = QueryActiveForeignKey(database, CliConsumer, out var hasCliRow);
            if (!string.IsNullOrWhiteSpace(cli))
            {
                return QueryAccount(database, cli);
            }

            // Preserve the account identity written by Unity Hub when the CLI
            // has not established an explicit active account of its own.
            if (!string.IsNullOrWhiteSpace(hub))
            {
                return QueryAccount(database, hub);
            }

            // A tombstone is consumer-specific. Only fall back to the shared
            // last-active account when neither Hub nor CLI has established an
            // explicit state yet.
            if (hasHubRow || hasCliRow)
            {
                return null;
            }

            var shared = QueryActiveForeignKey(database, SharedConsumer, out _);
            return string.IsNullOrWhiteSpace(shared) ? null : QueryAccount(database, shared);
        }
        finally
        {
            sqlite3_close(database);
        }
    }

    private static string? QueryActiveForeignKey(IntPtr database, string consumer, out bool hasRow)
    {
        var escapedConsumer = consumer.Replace("'", "''", StringComparison.Ordinal);
        var sql = Utf8Z($"SELECT foreign_key FROM active_account WHERE consumer = '{escapedConsumer}' LIMIT 1;");
        var result = sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero);
        if (result != SqliteOk || statement == IntPtr.Zero)
        {
            throw new IOException("Unity's shared account directory could not be queried.");
        }

        try
        {
            hasRow = sqlite3_step(statement) == SqliteRow;
            if (!hasRow || sqlite3_column_type(statement, 0) == 5)
            {
                return null;
            }

            var text = sqlite3_column_text(statement, 0);
            var byteCount = sqlite3_column_bytes(statement, 0);
            if (text == IntPtr.Zero || byteCount <= 0)
            {
                return null;
            }

            var bytes = new byte[byteCount];
            Marshal.Copy(text, bytes, 0, byteCount);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }

    private static UnitySharedAccount? QueryAccount(IntPtr database, string foreignKey)
    {
        var escapedForeignKey = foreignKey.Replace("'", "''", StringComparison.Ordinal);
        var sql = Utf8Z(
            $"SELECT foreign_key, name, email, primary_org FROM accounts WHERE foreign_key = '{escapedForeignKey}' LIMIT 1;");
        var result = sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero);
        if (result != SqliteOk || statement == IntPtr.Zero)
        {
            throw new IOException("Unity's shared account profile could not be queried.");
        }

        try
        {
            if (sqlite3_step(statement) != SqliteRow)
            {
                return null;
            }

            return new UnitySharedAccount(
                ReadSqliteText(statement, 0),
                ReadSqliteText(statement, 1),
                ReadSqliteText(statement, 2),
                ReadSqliteText(statement, 3));
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }

    private static string ReadSqliteText(IntPtr statement, int column)
    {
        if (sqlite3_column_type(statement, column) == 5)
        {
            return string.Empty;
        }

        var text = sqlite3_column_text(statement, column);
        var byteCount = sqlite3_column_bytes(statement, column);
        if (text == IntPtr.Zero || byteCount <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[byteCount];
        Marshal.Copy(text, bytes, 0, byteCount);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ReadChunkedCredential(string account)
    {
        var baseValue = ReadCredential(account);
        if (string.IsNullOrEmpty(baseValue)
            || !baseValue.Contains(ChunkManifestMarker, StringComparison.Ordinal))
        {
            return baseValue;
        }

        using var manifestDocument = JsonDocument.Parse(baseValue);
        var root = manifestDocument.RootElement;
        if (!root.TryGetProperty(ChunkManifestMarker, out var marker)
            || marker.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("gen", out var generationElement)
            || generationElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(generationElement.GetString())
            || !root.TryGetProperty("chunks", out var chunksElement)
            || !chunksElement.TryGetInt32(out var chunks)
            || chunks is <= 0 or > MaximumChunkCount
            || !root.TryGetProperty("length", out var lengthElement)
            || !lengthElement.TryGetInt32(out var expectedLength)
            || expectedLength < 0
            || expectedLength > chunks * 1024
            || !root.TryGetProperty("checksum", out var checksumElement)
            || checksumElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var generation = generationElement.GetString()!;
        var builder = new StringBuilder(expectedLength);
        for (var index = 0; index < chunks; index++)
        {
            var part = ReadCredential($"{account}--chunk--{generation}--{index}");
            if (part is null)
            {
                return null;
            }

            builder.Append(part);
        }

        var assembled = builder.ToString();
        return assembled.Length == expectedLength
               && string.Equals(
                   ComputeFnv1a(assembled),
                   checksumElement.GetString(),
                   StringComparison.OrdinalIgnoreCase)
            ? assembled
            : null;
    }

    private static string? ReadCredential(string account)
        => DecodeCredential(account + CredentialSuffix, Encoding.Unicode);

    private static string? ReadCredentialUtf8(string targetName)
        => DecodeCredential(targetName, Encoding.UTF8);

    private static string? DecodeCredential(string targetName, Encoding encoding)
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return encoding.GetString(bytes).TrimEnd('\0');
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static bool IsValidForeignKey(string value)
    {
        if (value.Length is 0 or > 512)
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '@' or '+' or '=' or '~' or '|' or '.' or '-' or '_');
    }

    private static string ComputeFnv1a(string value)
    {
        uint hash = 2_166_136_261;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16_777_619;
        }

        return hash.ToString("x");
    }

    private static byte[] Utf8Z(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private const int ChunkSize = 1024;

    private static bool WriteChunkedCredential(string account, string payload)
    {
        if (payload.Length <= 512)
        {
            return WriteCredential(account, payload);
        }

        var generation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var chunks = (int)Math.Ceiling((double)payload.Length / ChunkSize);
        var checksum = ComputeFnv1a(payload);

        for (var index = 0; index < chunks; index++)
        {
            var start = index * ChunkSize;
            var length = Math.Min(ChunkSize, payload.Length - start);
            var chunkPart = payload.Substring(start, length);
            var chunkName = $"{account}--chunk--{generation}--{index}";
            if (!WriteCredential(chunkName, chunkPart))
            {
                return false;
            }
        }

        var manifestJson = new System.Text.Json.Nodes.JsonObject
        {
            [ChunkManifestMarker] = true,
            ["gen"] = generation,
            ["chunks"] = chunks,
            ["length"] = payload.Length,
            ["checksum"] = checksum
        }.ToJsonString();

        return WriteCredential(account, manifestJson);
    }

    private static bool WriteCredential(string account, string value)
    {
        var targetName = account + CredentialSuffix;
        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        var blobPointer = Marshal.AllocHGlobal(bytes.Length);
        var targetPointer = Marshal.StringToHGlobalUni(targetName);
        var userNamePointer = Marshal.StringToHGlobalUni(account);
        try
        {
            Marshal.Copy(bytes, 0, blobPointer, bytes.Length);
            var cred = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                UserName = userNamePointer,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPointer,
                Persist = 2 // CRED_PERSIST_LOCAL_MACHINE
            };

            return CredWrite(ref cred, 0);
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blobPointer);
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(userNamePointer);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        [In] ref Credential credential,
        uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(
        byte[] filename,
        out IntPtr database,
        int flags,
        IntPtr virtualFileSystem);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr database,
        byte[] sql,
        int byteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);
}
