using System.Diagnostics;
using System.Text.Json;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityCliAuthService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);
    private readonly UnityCliToolService _toolService = new();

    public Task<UnityCliAuthState> GetStatusAsync(CancellationToken cancellationToken = default)
        => RunAuthCommandAsync("status", StatusTimeout, cancellationToken);

    public async Task<UnityCliAuthState> LoginAsync(CancellationToken cancellationToken = default)
    {
        var executablePath = await _toolService.GetVerifiedExecutablePathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return CliUnavailableState();
        }

        if (!NetworkConnectivityService.Current.CanAttemptInternet)
        {
            return new UnityCliAuthState(
                true,
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                NetworkConnectivityService.OfflineMessage);
        }

        using var loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loginTask = RunAuthCommandAsync(
            "login",
            LoginTimeout,
            loginCancellation.Token,
            executablePath);

        try
        {
            // Some CLI builds keep the login command alive after the browser has
            // already persisted the session. Poll the authoritative status so
            // the app can complete as soon as the callback succeeds.
            while (!loginTask.IsCompleted)
            {
                var completed = await Task.WhenAny(
                    loginTask,
                    Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
                if (completed == loginTask)
                {
                    break;
                }

                var polledStatus = await RunAuthCommandAsync(
                    "status",
                    StatusTimeout,
                    cancellationToken,
                    executablePath);
                if (polledStatus.IsLoggedIn)
                {
                    loginCancellation.Cancel();
                    await ObserveCanceledLoginAsync(loginTask);
                    return polledStatus with { Message = "Signed in securely through Unity CLI." };
                }
            }

            var loginResult = await loginTask;
            var status = await GetStatusAfterAuthAsync(
                executablePath,
                expectedLoggedIn: true,
                cancellationToken);
            return status.IsLoggedIn
                ? status with { Message = "Signed in securely through Unity CLI." }
                : loginResult.IsLoggedIn
                    ? loginResult
                    : status with
                    {
                        Message = IsUsefulFailure(loginResult.Message)
                            ? loginResult.Message
                            : status.Message
                    };
        }
        finally
        {
            if (!loginTask.IsCompleted)
            {
                loginCancellation.Cancel();
                await ObserveCanceledLoginAsync(loginTask);
            }
        }
    }

    public async Task<UnityCliAuthState> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var executablePath = await _toolService.GetVerifiedExecutablePathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return CliUnavailableState();
        }

        var logoutResult = await RunAuthCommandAsync(
            "logout",
            StatusTimeout,
            cancellationToken,
            executablePath);
        var status = await GetStatusAfterAuthAsync(
            executablePath,
            expectedLoggedIn: false,
            cancellationToken);
        return !status.IsLoggedIn
            ? status with { Message = "Signed out of Unity." }
            : status with
            {
                Message = IsUsefulFailure(logoutResult.Message)
                    ? logoutResult.Message
                    : "Unity CLI still reports an active account session."
            };
    }

    private async Task<UnityCliAuthState> RunAuthCommandAsync(
        string command,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken,
        string? verifiedExecutablePath = null)
    {
        var executablePath = verifiedExecutablePath
            ?? await _toolService.GetVerifiedExecutablePathAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return CliUnavailableState();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new(true, false, string.Empty, string.Empty, string.Empty,
                "Unity CLI could not be started.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await standardOutput;
            var error = await standardError;
            return ParseResult(command, process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return new(true, false, string.Empty, string.Empty, string.Empty,
                command == "login"
                    ? "Unity sign-in timed out. You can try again safely."
                    : "Unity CLI stopped responding.");
        }
        catch
        {
            TryTerminate(process);
            throw;
        }
    }

    private static UnityCliAuthState ParseResult(string command, int exitCode, string output, string error)
    {
        var cleanOutput = output.Trim();
        var cleanError = error.Trim();

        // 1. Handle Logout command specifically (Unity CLI outputs plain text on logout)
        if (command == "logout")
        {
            if (exitCode == 0 || cleanOutput.Contains("signed out", StringComparison.OrdinalIgnoreCase))
            {
                return new(true, false, string.Empty, string.Empty, string.Empty, "Not signed in");
            }
        }

        // 2. Try the final JSON document. Authentication commands can emit
        // progress records before the final result, so inspect records from
        // last to first when the complete output is not one JSON document.
        if (TryParseJsonResult(command, cleanOutput, out var parsedState))
        {
            return parsedState;
        }

        foreach (var line in cleanOutput.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (TryParseJsonResult(command, line, out parsedState))
            {
                return parsedState;
            }
        }

        if (command == "logout" || command == "status" || exitCode == 0)
        {
            return new(true, false, string.Empty, string.Empty, string.Empty, "Not signed in");
        }

        var message = !string.IsNullOrWhiteSpace(cleanError)
            ? cleanError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            : !string.IsNullOrWhiteSpace(cleanOutput)
                ? cleanOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                : "Not signed in";

        return new(true, false, string.Empty, string.Empty, string.Empty, message ?? "Not signed in");
    }

    private static bool TryParseJsonResult(
        string command,
        string json,
        out UnityCliAuthState state)
    {
        state = default!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var reportedLoggedIn = ReadBoolean(data, "loggedIn");
                var sessionState = ReadString(data, "sessionState") ?? string.Empty;
                var sessionIsStale = sessionState.Equals("stale", StringComparison.OrdinalIgnoreCase);
                var loggedIn = reportedLoggedIn && !sessionIsStale;
                var mode = ReadString(data, "mode") ?? string.Empty;
                var name = string.Empty;
                var email = string.Empty;
                if (data.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
                {
                    name = ReadString(user, "name") ?? string.Empty;
                    email = ReadString(user, "email") ?? string.Empty;
                }

                state = new(
                    true,
                    loggedIn,
                    name,
                    email,
                    mode,
                    loggedIn
                        ? "Signed in securely through Unity CLI."
                        : reportedLoggedIn && sessionIsStale
                            ? "Unity sign-in expired. Sign in again to continue."
                            : "Not signed in")
                {
                    SessionState = sessionState
                };
                return true;
            }

            var safeError = ReadSafeError(root);
            if (!string.IsNullOrWhiteSpace(safeError))
            {
                state = new(true, false, string.Empty, string.Empty, string.Empty, safeError);
                return true;
            }

            if (command == "status"
                && root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.True)
            {
                state = new(true, false, string.Empty, string.Empty, string.Empty, "Not signed in");
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsUsefulFailure(string message)
        => !string.IsNullOrWhiteSpace(message)
           && !message.Equals("Not signed in", StringComparison.OrdinalIgnoreCase)
           && !message.StartsWith("Signed ", StringComparison.OrdinalIgnoreCase);

    private async Task<UnityCliAuthState> GetStatusAfterAuthAsync(
        string executablePath,
        bool expectedLoggedIn,
        CancellationToken cancellationToken)
    {
        UnityCliAuthState? lastState = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            lastState = await RunAuthCommandAsync(
                "status",
                StatusTimeout,
                cancellationToken,
                executablePath);
            if (lastState.IsLoggedIn == expectedLoggedIn)
            {
                return lastState;
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        return lastState ?? CliUnavailableState();
    }

    private static async Task ObserveCanceledLoginAsync(Task<UnityCliAuthState> loginTask)
    {
        try
        {
            await loginTask;
        }
        catch (OperationCanceledException)
        {
            // Expected after status polling confirms browser sign-in.
        }
    }

    private static UnityCliAuthState CliUnavailableState()
        => new(false, false, string.Empty, string.Empty, string.Empty,
            "Install Unity CLI from Settings before signing in.");

    private static string? ReadSafeError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in errors.EnumerateArray())
        {
            var message = ReadString(item, "message");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may already have exited.
        }
    }
}
