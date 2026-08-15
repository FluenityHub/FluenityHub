using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    UnitySharedAccount Account);

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
    private const string ChunkManifestMarker = "__unityHubKeyringChunkedV1__";
    private const int MaximumChunkCount = 16_384;
    private const uint CredentialTypeGeneric = 1;
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;

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
                && unityExpirationElement.ValueKind == JsonValueKind.Number
                && unityExpirationElement.TryGetDouble(out var unityExpirationValue))
            {
                unityTokenExpiration = unityExpirationValue;
            }

            token = new UnitySharedAccessToken(
                accessTokenElement.GetString()!,
                expiration,
                unityToken,
                unityTokenExpiration,
                account);
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
            // FluenityHub observes both supported Unity consumers. Prefer an
            // explicitly active Hub session so a stale CLI pointer cannot make
            // the app start a competing OAuth refresh and invalidate the Hub's
            // in-memory session.
            var hub = QueryActiveForeignKey(database, HubConsumer, out var hasHubRow);
            if (!string.IsNullOrWhiteSpace(hub))
            {
                return QueryAccount(database, hub);
            }

            var cli = QueryActiveForeignKey(database, CliConsumer, out var hasCliRow);
            if (!string.IsNullOrWhiteSpace(cli))
            {
                return QueryAccount(database, cli);
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
    {
        var targetName = account + CredentialSuffix;
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
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

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
